using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Changes;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Octopushy.Tests.Definitions;

namespace Octopushy.Tests.Server;

/// <summary>A change made in a form is one commit to the lab repository, synced at once and followed to the end.</summary>
[Collection(PostgresCollection.Name)]
public class ChangeTests(PostgresFixture postgres)
{
    private const string Guest = SafetyBoundaryFixtures.Guest;
    private const string GuestFile = SafetyBoundaryFixtures.GuestFile;
    private const string Folder = "labs/home/proxmox/korriban/guests/krb-app-01";

    private const string Commented = """
        schemaVersion: 1
        # The application VM.
        type: vm
        host: korriban            # the only host
        vmid: 310
        hardware:
          cores: 2
          memory: 2048            # MiB
        network: { bridge: vmbr0, mode: static, address: 10.1.0.31/24, gateway: 10.1.0.1 }

        # Picked by hand.
        workflow: test-vm@1
        """;

    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "hardware": {
              "type": "object",
              "properties": {
                "cores": { "type": "integer", "minimum": 1 },
                "memory": { "description": "MiB", "type": "integer", "minimum": 256 }
              },
              "required": ["cores", "memory"],
              "additionalProperties": false
            },
            "network": { "type": "object" },
            "inputs": { "type": "object" }
          }
        }
        """;

    /// <summary>The minimal lab, its guest commented, its workflow with an inputs schema, and the lab's policy as given.</summary>
    private static TestRepository Lab(string policy = "")
    {
        var repository = TestRepository.Minimal();
        repository.Write(GuestFile, Commented);
        repository.Write("workflows/test-vm/workflow.yaml", """
            schemaVersion: 1
            version: "1.0"
            manages: vm
            description: A test VM.
            inputs: inputs.schema.json
            ui: { order: [hardware, network, inputs] }
            stages:
              - name: machine
                executor: opentofu
                root: ../../modules/opentofu/test-vm
                verify: proxmox
            """);
        repository.Write("workflows/test-vm/inputs.schema.json", Schema);
        if (policy.Length > 0)
            repository.Write("labs/home/lab.yaml", $"schemaVersion: 1\nname: Home\nexecution: {{ agentLabels: [home] }}\n{policy}\n");
        return repository;
    }

    /// <summary>
    /// The server with publishing on, reading a bare remote cloned from the lab, synced and trusted, and an operator's client.
    /// </summary>
    private sealed class Setup : IAsyncDisposable
    {
        public required ServerHarness Server { get; init; }
        public required HttpClient Client { get; init; }
        public required TestRepository Remote { get; init; }

        public string RemoteHead => TestWorkflows.Run(Remote.Root, "rev-parse", "main").Trim();

        public string RemoteFile(string path) => TestWorkflows.Run(Remote.Root, "show", $"main:{path}");

        /// <summary>Someone else's commit, pushed straight to the remote.</summary>
        public void PushElsewhere(string path, string content, string? message = null)
        {
            using var work = new TestRepository();
            TestWorkflows.Run(work.Root, "clone", "--quiet", Remote.Root, ".");
            work.Write(path, content);
            TestWorkflows.Run(work.Root, "add", "-A");
            TestWorkflows.Run(work.Root, "-c", "user.name=Other", "-c", "user.email=other@example.invalid", "commit", "--quiet", "-m", message ?? $"Edit {path}");
            TestWorkflows.Run(work.Root, "push", "--quiet", "origin", "HEAD:main");
        }

        /// <summary>
        /// Makes the remote run <paramref name="script"/> once, right after the next push it accepts: as another commit landing
        /// before Octopushy's own sync, or a push that is slow to finish.
        /// </summary>
        public void AfterNextPush(string script)
        {
            var path = Path.Combine(Remote.Root, "hooks", "post-receive");
            File.WriteAllText(path, "#!/bin/sh\nset -e\n[ -e hook-ran ] && exit 0\ntouch hook-ran\n" + script + "\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        /// <summary>A script for <see cref="AfterNextPush"/> that commits <paramref name="sed"/> applied to <paramref name="file"/> on main.</summary>
        public static string CommitEdit(string file, string sed) => $"""
            export GIT_INDEX_FILE="$PWD/hook-index" GIT_AUTHOR_NAME=Other GIT_AUTHOR_EMAIL=other@example.invalid
            export GIT_COMMITTER_NAME=Other GIT_COMMITTER_EMAIL=other@example.invalid
            git read-tree main
            blob=$(git show main:{file} | sed '{sed}' | git hash-object -w --stdin)
            git update-index --cacheinfo 100644,$blob,{file}
            commit=$(git commit-tree $(git write-tree) -p main -m 'Edit {file} elsewhere')
            git update-ref refs/heads/main $commit
            rm -f "$GIT_INDEX_FILE"
            """;

        /// <summary>
        /// A commit a push attempt would have prepared, on <paramref name="start"/> (main by default), with the guest's memory set and
        /// a note added; it is kept on a side branch of the remote, so main does not have it until a test moves main there.
        /// </summary>
        public string Prepare(string memory, string? start = null, string note = "")
        {
            using var work = new TestRepository();
            TestWorkflows.Run(work.Root, "clone", "--quiet", Remote.Root, ".");
            if (start is not null)
                TestWorkflows.Run(work.Root, "checkout", "--quiet", start);
            var text = File.ReadAllText(Path.Combine(work.Root, GuestFile));
            work.Write(GuestFile, System.Text.RegularExpressions.Regex.Replace(text, "memory: [0-9]+ ", $"memory: {memory} ").TrimEnd('\n') + note);
            TestWorkflows.Run(work.Root, "-c", "user.name=Ops", "-c", "user.email=ops@octopushy.invalid", "commit", "--quiet", "-am", "Prepared");
            var commit = TestWorkflows.Run(work.Root, "rev-parse", "HEAD").Trim();
            TestWorkflows.Run(work.Root, "push", "--quiet", "origin", $"{commit}:refs/heads/prepared-{commit[..12]}");
            return commit;
        }

        /// <summary>Moves main, as a push or a force-push would.</summary>
        public void MoveMain(string commit) => TestWorkflows.Run(Remote.Root, "update-ref", "refs/heads/main", commit);

        public void DeleteElsewhere(string path, string message)
        {
            using var work = new TestRepository();
            TestWorkflows.Run(work.Root, "clone", "--quiet", Remote.Root, ".");
            TestWorkflows.Run(work.Root, "rm", "-r", "--quiet", path);
            TestWorkflows.Run(work.Root, "-c", "user.name=Other", "-c", "user.email=other@example.invalid", "commit", "--quiet", "-m", message);
            TestWorkflows.Run(work.Root, "push", "--quiet", "origin", "HEAD:main");
        }

        public async Task<ChangeDto> GetAsync(Guid id) => (await Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{id}"))!;

        public async Task<ChangeFormDto> FormAsync(string address = Guest)
        {
            var resource = await Server.ResourceAsync(address);
            return (await Client.GetFromJsonAsync<ChangeFormDto>($"/api/changes/form/{resource.Id}"))!;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
            Remote.Dispose();
        }
    }

    private async Task<Setup> StartAsync(TestRepository? lab = null, bool publish = true, FakeAgentHolder? agent = null,
        Dictionary<string, string>? settings = null, TimeProvider? clock = null)
    {
        var all = new Dictionary<string, string>(settings ?? []);
        if (publish)
            all["Octopushy:RepositoryEditor:PublishEnabled"] = "true";
        var server = await ServerHarness.StartAsync(postgres, lab ?? Lab(), settings: all, clock: clock);
        var remote = new TestRepository();
        await server.Services.GetRequiredService<GitCli>().RunAsync(["clone", "--bare", "--quiet", server.Repository.Root, remote.Root]);
        await server.WithDbAsync(async db =>
        {
            (await db.RepositorySettings.SingleAsync()).Url = remote.Root;
            await db.SaveChangesAsync();
        });
        if (agent is not null)
            agent.Agent = await server.ConnectAgentAsync("home");
        var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        return new Setup { Server = server, Client = client, Remote = remote };
    }

    private sealed class FakeAgentHolder
    {
        public FakeAgent? Agent { get; set; }
    }

    private static JsonObject Hardware(int cores, int memory) => new() { ["cores"] = cores, ["memory"] = memory };

    private static async Task<ChangeDto> CommitAsync(Setup setup, object request)
    {
        var response = await setup.Client.PostAsJsonAsync("/api/changes", request);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ChangeDto>())!;
    }

    private static async Task<ChangeRefusedDto> RefusedAsync(Setup setup, object request)
    {
        var response = await setup.Client.PostAsJsonAsync("/api/changes", request);
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ChangeRefusedDto>())!;
    }

    [Fact]
    public async Task An_edit_is_one_commit_by_the_operator_that_keeps_comments_and_is_synced_at_once()
    {
        await using var setup = await StartAsync();
        var form = await setup.FormAsync();
        Assert.Null(form.BlockedReason);
        Assert.Equal(setup.RemoteHead, form.BaseCommit);
        Assert.Equal(GuestFile, form.File);
        Assert.Equal("test-vm@1", form.Workflow);
        Assert.Equal(2048, form.Hardware!["memory"]!.GetValue<int>());
        Assert.Equal("MiB", form.Resolved!.Inputs!["properties"]!["hardware"]!["properties"]!["memory"]!["description"]!.GetValue<string>());
        Assert.Equal("hardware", form.Resolved.Ui!["order"]![0]!.GetValue<string>());

        var request = new ChangeRequest(form.ResourceId, form.BaseCommit!, Hardware: Hardware(2, 4096), Network: form.Network!.AsObject());
        var preview = (await (await setup.Client.PostAsJsonAsync("/api/changes/preview", request)).Content.ReadFromJsonAsync<ChangePreviewDto>())!;
        Assert.True(preview.Valid);
        Assert.Null(preview.Refused);
        Assert.Equal("krb-app-01: memory 2048 → 4096", preview.Subject);
        Assert.Equal("krb-app-01: memory 2048 → 4096\n\nOctopushy-Change: <given when committed>\n", preview.Message);
        Assert.Contains("-  memory: 2048            # MiB\n+  memory: 4096            # MiB\n", preview.Diff);
        Assert.Equal(2, preview.Diff.Split('\n').Count(l => l.StartsWith('+') || l.StartsWith('-')) - 2);
        Assert.Equal(form.BaseCommit, setup.RemoteHead);

        var change = await CommitAsync(setup, request);
        Assert.Equal(ChangeKinds.Edit, change.Kind);
        Assert.Equal("ops", change.Author);
        Assert.Equal(setup.RemoteHead, change.Commit);
        Assert.Equal(Commented.Replace("memory: 2048 ", "memory: 4096 ") + "\n", setup.RemoteFile(GuestFile));
        var log = TestWorkflows.Run(setup.Remote.Root, "log", "-1", "--format=%an <%ae>|%cn|%P|%B", "main");
        Assert.Equal($"ops <ops@octopushy.invalid>|Octopushy editor|{form.BaseCommit}|krb-app-01: memory 2048 → 4096\n\nOctopushy-Change: {change.Id}", log.Trim());

        // Synced at once, not at the next poll: the revision is loaded and has proposed the guest's run, which waits for an agent.
        Assert.NotNull(change.RevisionId);
        var revision = await setup.Server.QueryAsync(db => db.Revisions.AsNoTracking().SingleAsync(r => r.Id == change.RevisionId));
        Assert.Equal(change.Commit, revision.Commit);
        var run = await setup.Server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(change.RevisionId, run.RevisionId);
        Assert.Equal([run.Id], change.Runs.Select(r => r.Id));
        Assert.Equal(ChangeStatuses.Planning, change.Status);

        var listed = (await setup.Client.GetFromJsonAsync<List<ChangeDto>>($"/api/changes?resourceId={change.ResourceId}"))!;
        Assert.Equal([change.Id], listed.Select(c => c.Id));
    }

    [Fact]
    public async Task A_change_is_refused_when_its_file_moved_and_merged_when_only_another_file_did()
    {
        await using var setup = await StartAsync();
        var form = await setup.FormAsync();
        setup.PushElsewhere("labs/home/lab.yaml", "schemaVersion: 1\nname: Home lab\nexecution:\n  agentLabels: [home]\n");
        var elsewhere = setup.RemoteHead;

        var merged = await CommitAsync(setup, new ChangeRequest(form.ResourceId, form.BaseCommit!, Hardware: Hardware(4, 2048)));
        Assert.Equal(elsewhere, TestWorkflows.Run(setup.Remote.Root, "log", "-1", "--format=%P", "main").Trim());
        Assert.Contains("name: Home lab", setup.RemoteFile("labs/home/lab.yaml"));
        Assert.Contains("  cores: 4\n", setup.RemoteFile(GuestFile));
        Assert.Equal(merged.Commit, setup.RemoteHead);

        form = await setup.FormAsync();
        setup.PushElsewhere(GuestFile, setup.RemoteFile(GuestFile).TrimEnd('\n') + "\n# A note.");
        var moved = setup.RemoteHead;
        var refused = await RefusedAsync(setup, new ChangeRequest(form.ResourceId, form.BaseCommit!, Hardware: Hardware(4, 8192)));
        Assert.Equal(ChangeRefusal.Reasons.Stale, refused.Reason);
        Assert.StartsWith($"krb-app-01 changed since the form was loaded: {GuestFile} is different at {moved[..10]}. Reload it", refused.Message);
        Assert.Equal(moved, setup.RemoteHead);
        var recorded = (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{refused.ChangeId}"))!;
        Assert.Equal(ChangeStatuses.Refused, recorded.Status);
        Assert.Null(recorded.Commit);
    }

    [Fact]
    public async Task A_change_that_does_not_validate_is_refused_with_its_issues_on_their_fields()
    {
        await using var setup = await StartAsync();
        var form = await setup.FormAsync();
        var request = new ChangeRequest(form.ResourceId, form.BaseCommit!, Hardware: Hardware(2, 64));

        var preview = (await (await setup.Client.PostAsJsonAsync("/api/changes/preview", request)).Content.ReadFromJsonAsync<ChangePreviewDto>())!;
        Assert.False(preview.Valid);
        Assert.Contains(preview.FieldIssues, i => i.Pointer == "/hardware/memory");

        var refused = await RefusedAsync(setup, request);
        Assert.Equal(ChangeRefusal.Reasons.Invalid, refused.Reason);
        Assert.Contains(refused.Issues, i => i.File == GuestFile && i.Severity == "error" && i.Message.Contains("memory"));
        Assert.Contains(refused.FieldIssues, i => i.Pointer == "/hardware/memory");
        Assert.Equal(form.BaseCommit, setup.RemoteHead);

        var workflow = await RefusedAsync(setup, request with { Hardware = null, Workflow = "test-vm" });
        Assert.Equal(ChangeRefusal.Reasons.Invalid, workflow.Reason);
        Assert.Equal("/workflow", Assert.Single(workflow.FieldIssues).Pointer);
        var nothing = await RefusedAsync(setup, request with { Hardware = Hardware(2, 2048) });
        Assert.Equal(ChangeRefusal.Reasons.NoChange, nothing.Reason);
    }

    [Fact]
    public async Task A_changes_run_is_followed_through_its_approval_to_applied()
    {
        var holder = new FakeAgentHolder();
        await using var setup = await StartAsync(Lab("policy: { approval: manual }"), agent: holder);
        var agent = holder.Agent!;
        var form = await setup.FormAsync();
        var change = await CommitAsync(setup, new ChangeRequest(form.ResourceId, form.BaseCommit!, Hardware: Hardware(2, 4096)));
        Assert.Equal(ChangeStatuses.Planning, change.Status);

        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await setup.Server.TickAsync();
        change = (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{change.Id}"))!;
        Assert.Equal(ChangeStatuses.WaitingForApproval, change.Status);

        var run = await setup.Server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(change.Runs.Single().Id, run.Id);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        (await setup.Client.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval.Id}/approve", new { planSha256 = approval.PlanSha256 }))
            .EnsureSuccessStatusCode();
        var apply = await agent.NextAsync("apply");
        change = (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{change.Id}"))!;
        Assert.Equal(ChangeStatuses.Applying, change.Status);
        await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());

        change = (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{change.Id}"))!;
        Assert.Equal(ChangeStatuses.Applied, change.Status);
        Assert.Equal(RunStatuses.Succeeded, change.Runs.Single().Status);
        Assert.Equal(ChangeStatuses.Applied, (await setup.Server.QueryAsync(db => db.Changes.SingleAsync())).Status);
    }

    [Fact]
    public async Task Removing_a_guest_deletes_its_folder_and_proposes_its_retire_run()
    {
        var holder = new FakeAgentHolder();
        await using var setup = await StartAsync(agent: holder);
        var agent = holder.Agent!;
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        Assert.Equal(SafetyBoundaryFixtures.Vmid, (await setup.Server.ResourceAsync(Guest)).BoundVmid);
        setup.PushElsewhere($"{Folder}/motd", "hello");
        await setup.Server.SyncAsync();
        var form = await setup.FormAsync();
        Assert.True(form.CanRemove);

        var change = await CommitAsync(setup, new ChangeRequest(form.ResourceId, form.BaseCommit!, Kind: ChangeKinds.Remove));
        Assert.Equal(ChangeKinds.Remove, change.Kind);
        Assert.Equal("krb-app-01: remove the guest", change.Message);
        Assert.DoesNotContain(Folder, TestWorkflows.Run(setup.Remote.Root, "ls-tree", "-r", "--name-only", "main"));
        Assert.NotNull((await setup.Server.ResourceAsync(Guest)).MissingSince);
        var retire = await setup.Server.LatestRunAsync(Guest, Operations.Retire);
        Assert.Equal([retire.Id], change.Runs.Select(r => r.Id));
        Assert.Equal(ChangeStatuses.Planning, change.Status);
        Assert.Equal([$"{Folder}/guest.yaml", $"{Folder}/motd"], change.Files);
    }

    private static async Task ProvisionAsync(Setup setup, FakeAgent agent)
    {
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        Assert.Equal(SafetyBoundaryFixtures.Vmid, (await setup.Server.ResourceAsync(Guest)).BoundVmid);
    }

    [Fact]
    public async Task A_commit_that_moves_the_value_on_before_the_sync_supersedes_the_change_and_it_is_never_applied()
    {
        var holder = new FakeAgentHolder();
        await using var setup = await StartAsync(agent: holder);
        var agent = holder.Agent!;
        await ProvisionAsync(setup, agent);
        setup.AfterNextPush(Setup.CommitEdit(GuestFile, "s/memory: 4096 /memory: 8192 /"));
        var form = await setup.FormAsync();

        var change = await CommitAsync(setup, new ChangeRequest(form.ResourceId, form.BaseCommit!, Hardware: Hardware(2, 4096)));
        var later = setup.RemoteHead;
        Assert.NotEqual(change.Commit, later);
        Assert.Contains("memory: 8192 ", setup.RemoteFile(GuestFile));
        Assert.Equal("superseded", change.Status);
        Assert.Equal(later, change.SupersededBy);
        Assert.Contains(later[..10], change.Detail);
        Assert.Empty(change.Runs);

        // The later commit's own run goes through; the change it superseded is never applied by it.
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact("later"));
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        Assert.Equal(RunStatuses.Succeeded, (await setup.Server.LatestRunAsync(Guest, Operations.Provision)).Status);
        change = (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{change.Id}"))!;
        Assert.Equal("superseded", change.Status);
        Assert.Empty(change.Runs);
    }

    [Fact]
    public async Task A_later_commit_that_keeps_the_change_is_followed_in_its_place()
    {
        await using var setup = await StartAsync();
        setup.AfterNextPush(Setup.CommitEdit("labs/home/lab.yaml", "s/name: Home/name: Home lab/"));
        var form = await setup.FormAsync();
        var change = await CommitAsync(setup, new ChangeRequest(form.ResourceId, form.BaseCommit!, Hardware: Hardware(2, 4096)));
        Assert.NotEqual(change.Commit, setup.RemoteHead);
        var revision = await setup.Server.QueryAsync(db => db.Revisions.AsNoTracking().SingleAsync(r => r.Id == change.RevisionId));
        Assert.Equal(setup.RemoteHead, revision.Commit);
        Assert.Equal(ChangeStatuses.Planning, change.Status);
        Assert.Single(change.Runs);
    }

    [Fact]
    public async Task A_change_is_recorded_before_its_push_so_a_request_that_dies_after_it_still_ends_in_its_commit()
    {
        await using var setup = await StartAsync();
        var started = Path.Combine(setup.Remote.Root, "push-received");
        setup.AfterNextPush($"touch '{started}'\nsleep 20");
        var form = await setup.FormAsync();
        using var abort = new CancellationTokenSource();
        var request = setup.Client.PostAsJsonAsync("/api/changes", new ChangeRequest(form.ResourceId, form.BaseCommit!, Hardware: Hardware(2, 4096)), abort.Token);
        for (var i = 0; i < 300 && !File.Exists(started); i++)
            await Task.Delay(100);
        Assert.True(File.Exists(started), "The push never reached the remote.");
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        // The branch moved, and the change that moved it is on record, whatever happened to the request.
        var pushed = setup.RemoteHead;
        Assert.NotEqual(form.BaseCommit, pushed);
        var id = Guid.Parse(TestWorkflows.Run(setup.Remote.Root, "log", "-1", "--format=%(trailers:key=Octopushy-Change,valueonly)", "main").Trim());
        ResourceChange? row = null;
        for (var i = 0; i < 100 && row is null; i++)
        {
            row = await setup.Server.QueryAsync(db => db.Changes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id));
            if (row is null) await Task.Delay(100);
        }

        Assert.NotNull(row);
        await setup.Server.SyncAsync();
        var change = (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{id}"))!;
        Assert.Equal(pushed, change.Commit);
        Assert.Equal(ChangeStatuses.Planning, change.Status);
        Assert.Single(change.Runs);
    }

    [Fact]
    public async Task An_unsettled_change_is_found_only_by_its_own_commits_stays_unknown_until_then_and_can_be_dismissed()
    {
        await using var setup = await StartAsync();
        var form = await setup.FormAsync();
        var late = setup.Prepare("4096");
        // On top of the first, so moving main to it keeps both on the branch.
        var other = setup.Prepare("8192", late);
        var arriving = await PendingAsync(setup, form, candidates: [late]);
        var wrong = await PendingAsync(setup, form, candidates: [other]);
        var never = await PendingAsync(setup, form, candidates: [new string('e', 40)]);
        var unpushed = await PendingAsync(setup, form);

        await setup.Server.SyncAsync();
        async Task<ChangeDto> Get(Guid id) => (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{id}"))!;
        foreach (var id in new[] { arriving, wrong, never })
        {
            var pending = await Get(id);
            Assert.Equal(ChangeStatuses.Unknown, pending.Status);
            Assert.False(pending.Final);
            Assert.False(pending.NotCommitted);
        }

        var refused = await Get(unpushed);
        Assert.Equal(ChangeStatuses.Refused, refused.Status);
        Assert.True(refused.Final && refused.NotCommitted);

        // A late arrival is taken, by reachability, and held to what the change wrote like any other.
        TestWorkflows.Run(setup.Remote.Root, "update-ref", "refs/heads/main", late);
        await setup.Server.SyncAsync();
        var arrived = await Get(arriving);
        Assert.Equal(late, arrived.Commit);
        Assert.Equal(ChangeStatuses.Planning, arrived.Status);
        TestWorkflows.Run(setup.Remote.Root, "update-ref", "refs/heads/main", other);
        await setup.Server.SyncAsync();
        var held = await Get(wrong);
        Assert.Equal(other, held.Commit);
        Assert.Equal(ChangeStatuses.Superseded, held.Status);

        // A person settles the one that will not arrive, and is recorded with it.
        var dismissed = await (await setup.Client.PostAsync($"/api/changes/{never}/dismiss", null)).Content.ReadFromJsonAsync<ChangeDto>();
        Assert.Equal(ChangeStatuses.Dismissed, dismissed!.Status);
        Assert.Equal("ops", dismissed.DismissedBy);
        // A push was attempted: dismissing stops the waiting, and is neither final nor proof.
        Assert.False(dismissed.Final || dismissed.NotCommitted);
        Assert.Equal(HttpStatusCode.Conflict, (await setup.Client.PostAsync($"/api/changes/{arriving}/dismiss", null)).StatusCode);
    }

    /// <summary>A change on record as if its request had ended mid-push, wanting memory 4096.</summary>
    private static async Task<Guid> PendingAsync(Setup setup, ChangeFormDto form, string status = "committing", TimeSpan? age = null,
        List<string>? candidates = null)
    {
        var id = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow - (age ?? TimeSpan.FromMinutes(1));
        await setup.Server.WithDbAsync(async db =>
        {
            db.Changes.Add(new ResourceChange
            {
                Id = id, Kind = ChangeKinds.Edit, ResourceKind = ResourceKinds.Guest, ResourceId = form.ResourceId, Address = Guest, Author = "ops",
                BaseCommit = form.BaseCommit!, Message = "krb-app-01: memory 2048 → 4096", Status = status, Files = [GuestFile],
                Edited = """{"hardware":{"cores":2,"memory":4096}}""", CreatedAt = at, UpdatedAt = at, CandidateCommits = candidates ?? [],
            });
            await db.SaveChangesAsync();
        });
        return id;
    }

    [Fact]
    public async Task A_commit_that_quotes_a_change_in_its_message_is_never_taken_for_its_commit()
    {
        await using var setup = await StartAsync();
        var form = await setup.FormAsync();
        var id = await PendingAsync(setup, form);
        // Someone's commit quotes the change and sets memory otherwise.
        setup.PushElsewhere(GuestFile, Commented.Replace("memory: 2048 ", "memory: 8192 ").TrimEnd('\n'),
            $"Resize by hand, instead of\n\nOctopushy-Change: {id}");

        await setup.Server.SyncAsync();
        var change = (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{id}"))!;
        Assert.Null(change.Commit);
        Assert.NotEqual(ChangeStatuses.Committed, change.Status);
        Assert.NotEqual(ChangeStatuses.Applied, change.Status);
        Assert.Empty(change.Runs);
    }

    [Fact]
    public async Task A_push_that_landed_but_lost_its_acknowledgement_is_a_success()
    {
        await using var setup = await StartAsync();
        // The remote takes the update, then its receive-pack dies before saying so.
        setup.AfterNextPush("kill -9 $PPID");
        var form = await setup.FormAsync();
        var change = await CommitAsync(setup, new ChangeRequest(form.ResourceId, form.BaseCommit!, Hardware: Hardware(2, 4096)));
        Assert.Equal(setup.RemoteHead, change.Commit);
        Assert.Contains("  memory: 4096 ", setup.RemoteFile(GuestFile));
        Assert.Equal(ChangeStatuses.Planning, change.Status);
        Assert.Single(change.Runs);
    }

    [Fact]
    public async Task A_crash_between_recording_a_revision_and_proposing_its_runs_is_finished_by_the_next_sync()
    {
        await using var setup = await StartAsync();
        var faults = setup.Server.Services.GetRequiredService<InventoryFaults>();
        faults.BeforeRuns = () => throw new IOException("The server stopped here.");
        var form = await setup.FormAsync();
        var change = await CommitAsync(setup, new ChangeRequest(form.ResourceId, form.BaseCommit!, Hardware: Hardware(2, 4096)));
        Assert.Equal(ChangeStatuses.Committed, change.Status);
        Assert.Null(change.RevisionId);

        faults.BeforeRuns = null;
        await setup.Server.SyncAsync();
        change = (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{change.Id}"))!;
        Assert.Equal(ChangeStatuses.Planning, change.Status);
        var run = Assert.Single(change.Runs);
        Assert.Equal(change.Commit, run.Commit);
    }

    [Fact]
    public async Task A_head_revision_without_its_inventory_marker_is_applied_again_and_only_what_is_missing_is_proposed()
    {
        await using var setup = await StartAsync();
        var head = await setup.Server.QueryAsync(db => RepositoryTrust.HeadRevisions(db).AsNoTracking().SingleAsync());
        Assert.NotNull(head.InventoryAppliedAt);
        // What a crash left before the marker existed: the head and its desired inputs recorded, its run never proposed.
        await setup.Server.WithDbAsync(async db =>
        {
            db.Runs.RemoveRange(await db.Runs.ToListAsync());
            (await db.Revisions.SingleAsync(r => r.Id == head.Id)).InventoryAppliedAt = null;
            await db.SaveChangesAsync();
        });

        await setup.Server.SyncAsync();
        var run = await setup.Server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(head.Id, run.RevisionId);
        Assert.NotNull((await setup.Server.QueryAsync(db => db.Revisions.AsNoTracking().SingleAsync(r => r.Id == head.Id))).InventoryAppliedAt);

        // Applied once more, it proposes nothing it already has.
        await setup.Server.WithDbAsync(async db =>
        {
            (await db.Revisions.SingleAsync(r => r.Id == head.Id)).InventoryAppliedAt = null;
            await db.SaveChangesAsync();
        });
        await setup.Server.SyncAsync();
        Assert.Equal(1, await setup.Server.QueryAsync(db => db.Runs.CountAsync()));
    }

    [Fact]
    public async Task A_dismissed_change_is_not_proof_it_was_not_committed_and_a_late_arrival_moves_it_on()
    {
        await using var setup = await StartAsync();
        var form = await setup.FormAsync();
        var late = setup.Prepare("4096");
        var id = await PendingAsync(setup, form, candidates: [late]);
        await setup.Server.SyncAsync();
        Assert.Equal(ChangeStatuses.Unknown, (await setup.GetAsync(id)).Status);

        var dismissed = (await (await setup.Client.PostAsync($"/api/changes/{id}/dismiss", null)).Content.ReadFromJsonAsync<ChangeDto>())!;
        Assert.Equal(ChangeStatuses.Dismissed, dismissed.Status);
        Assert.Equal("ops", dismissed.DismissedBy);
        // A push was attempted: dismissing stops the waiting, and proves nothing.
        Assert.False(dismissed.NotCommitted);

        // The push that was still under way lands after all.
        setup.MoveMain(late);
        await setup.Server.SyncAsync();
        var landed = await setup.GetAsync(id);
        Assert.Equal(late, landed.Commit);
        Assert.Equal(ChangeStatuses.Planning, landed.Status);
        Assert.False(landed.NotCommitted);
        Assert.Single(landed.Runs);
    }

    [Fact]
    public async Task Work_recorded_without_its_run_is_proposed_even_when_a_later_revision_changes_nothing_for_it()
    {
        await using var setup = await StartAsync();
        var first = await setup.Server.QueryAsync(db => RepositoryTrust.HeadRevisions(db).AsNoTracking().SingleAsync());
        // What a crash left: the guest's desired inputs recorded for the first revision, its run never proposed.
        await setup.Server.WithDbAsync(async db =>
        {
            db.Runs.RemoveRange(await db.Runs.ToListAsync());
            (await db.Revisions.SingleAsync(r => r.Id == first.Id)).InventoryAppliedAt = null;
            await db.SaveChangesAsync();
        });
        // A commit that changes nothing a run is built from arrives before anything recovers.
        setup.PushElsewhere(GuestFile, Commented.TrimEnd('\n') + "\ndescription: The application VM.");
        await setup.Server.SyncAsync();

        var run = await setup.Server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(setup.RemoteHead, run.Commit);
    }

    [Fact]
    public async Task A_removal_recorded_without_its_retire_run_is_proposed_when_the_revision_is_applied_again()
    {
        var holder = new FakeAgentHolder();
        await using var setup = await StartAsync(agent: holder);
        await ProvisionAsync(setup, holder.Agent!);
        setup.DeleteElsewhere(Folder, "Remove the guest");
        await setup.Server.SyncAsync();
        var head = await setup.Server.QueryAsync(db => RepositoryTrust.HeadRevisions(db).AsNoTracking().SingleAsync());
        await setup.Server.WithDbAsync(async db =>
        {
            db.Runs.RemoveRange(await db.Runs.Where(r => r.Operation == Operations.Retire).ToListAsync());
            (await db.Revisions.SingleAsync(r => r.Id == head.Id)).InventoryAppliedAt = null;
            await db.SaveChangesAsync();
        });

        await setup.Server.SyncAsync();
        var retire = await setup.Server.LatestRunAsync(Guest, Operations.Retire);
        Assert.Equal(head.Commit, RunContext.Parse(retire.Context).DefinitionRemovedIn);
    }

    [Fact]
    public async Task A_late_arrival_is_followed_however_many_revisions_came_before_it()
    {
        await using var setup = await StartAsync();
        var form = await setup.FormAsync();
        var late = setup.Prepare("4096");
        var id = await PendingAsync(setup, form, candidates: [late]);
        for (var i = 0; i < 21; i++)
        {
            setup.PushElsewhere("labs/home/lab.yaml", $"schemaVersion: 1\nname: Home {i}\nexecution:\n  agentLabels: [home]");
            await setup.Server.SyncAsync();
        }

        setup.MoveMain(late);
        await setup.Server.SyncAsync();
        var change = await setup.GetAsync(id);
        Assert.Equal(late, change.Commit);
        Assert.NotNull(change.RevisionId);
        Assert.Equal(ChangeStatuses.Planning, change.Status);
        Assert.Single(change.Runs);
    }

    [Fact]
    public async Task A_commit_a_force_push_removes_before_it_is_followed_is_unknown_again()
    {
        await using var setup = await StartAsync();
        var faults = setup.Server.Services.GetRequiredService<InventoryFaults>();
        faults.BeforeRuns = () => throw new IOException("The server stopped here.");
        var form = await setup.FormAsync();
        var change = await CommitAsync(setup, new ChangeRequest(form.ResourceId, form.BaseCommit!, Hardware: Hardware(2, 4096)));
        Assert.Equal(ChangeStatuses.Committed, change.Status);

        faults.BeforeRuns = null;
        setup.MoveMain(form.BaseCommit!);
        await setup.Server.SyncAsync();
        change = await setup.GetAsync(change.Id);
        Assert.Equal(ChangeStatuses.Unknown, change.Status);
        Assert.Null(change.Commit);
        Assert.Empty(change.Runs);
    }

    [Fact]
    public async Task Reconciliation_checks_a_bounded_batch_each_sync_and_reaches_every_change_in_turn()
    {
        await using var setup = await StartAsync(settings: new() { ["Octopushy:Changes:ReconcileBatch"] = "2" });
        var form = await setup.FormAsync();
        var one = setup.Prepare("4096");
        var two = setup.Prepare("4096", one, "\n# two");
        var three = setup.Prepare("4096", two, "\n# three");
        foreach (var candidate in new[] { one, two, three })
            await PendingAsync(setup, form, candidates: [candidate]);
        setup.MoveMain(three);

        await setup.Server.SyncAsync();
        Assert.Equal(2, await setup.Server.QueryAsync(db => db.Changes.CountAsync(c => c.Commit != null)));
        await setup.Server.SyncAsync();
        Assert.Equal(3, await setup.Server.QueryAsync(db => db.Changes.CountAsync(c => c.Commit != null)));
    }

    [Fact]
    public async Task Returning_to_earlier_inputs_is_proposed_again_when_its_run_is_missing_whatever_ran_for_them_before()
    {
        await using var setup = await StartAsync();
        await CommitAsync(setup, new ChangeRequest((await setup.FormAsync()).ResourceId, setup.RemoteHead, Hardware: Hardware(2, 4096)));
        var back = await CommitAsync(setup, new ChangeRequest((await setup.FormAsync()).ResourceId, setup.RemoteHead, Hardware: Hardware(2, 2048)));
        // The first run for 2048 was cancelled when 4096 superseded it; the run for coming back to 2048 is then lost.
        await setup.Server.WithDbAsync(async db =>
        {
            db.Runs.RemoveRange(await db.Runs.Where(r => r.RevisionId == back.RevisionId).ToListAsync());
            (await db.Revisions.SingleAsync(r => r.Id == back.RevisionId)).InventoryAppliedAt = null;
            await db.SaveChangesAsync();
        });

        await setup.Server.SyncAsync();
        var runs = await setup.Server.QueryAsync(db => db.Runs.AsNoTracking().ToListAsync());
        Assert.Contains(runs, r => r.RevisionId == back.RevisionId && r.Operation == Operations.Provision && !RunStatuses.IsTerminal(r.Status));
    }

    [Fact]
    public async Task A_second_removal_is_proposed_again_when_its_run_is_missing_whatever_ran_for_the_first()
    {
        await using var setup = await StartAsync();
        var definition = setup.RemoteFile(GuestFile);
        setup.DeleteElsewhere(Folder, "Remove the guest");
        await setup.Server.SyncAsync();
        // Restored, which cancels that removal, then removed again.
        setup.PushElsewhere(GuestFile, definition.TrimEnd('\n'));
        await setup.Server.SyncAsync();
        setup.DeleteElsewhere(Folder, "Remove the guest again");
        await setup.Server.SyncAsync();
        var head = await setup.Server.QueryAsync(db => RepositoryTrust.HeadRevisions(db).AsNoTracking().SingleAsync());
        var retires = await setup.Server.QueryAsync(db => db.Runs.AsNoTracking().Where(r => r.Operation == Operations.Retire).ToListAsync());
        Assert.Equal(2, retires.Count);
        Assert.Equal(retires[0].InputsHash, retires[1].InputsHash);
        await setup.Server.WithDbAsync(async db =>
        {
            db.Runs.RemoveRange(await db.Runs.Where(r => r.Operation == Operations.Retire && r.Status != RunStatuses.Cancelled).ToListAsync());
            (await db.Revisions.SingleAsync(r => r.Id == head.Id)).InventoryAppliedAt = null;
            await db.SaveChangesAsync();
        });

        await setup.Server.SyncAsync();
        var retire = await setup.Server.LatestRunAsync(Guest, Operations.Retire);
        Assert.Equal(head.Commit, RunContext.Parse(retire.Context).DefinitionRemovedIn);
        Assert.False(RunStatuses.IsTerminal(retire.Status));
    }

    [Theory]
    [InlineData(RunStatuses.Succeeded)]
    [InlineData(RunStatuses.AwaitingApproval)]
    [InlineData(RunStatuses.Running)]
    public async Task Applying_an_unmarked_head_again_does_not_duplicate_the_work_it_has(string status)
    {
        await using var setup = await StartAsync();
        var change = await CommitAsync(setup, new ChangeRequest((await setup.FormAsync()).ResourceId, setup.RemoteHead, Hardware: Hardware(2, 4096)));
        await setup.Server.WithDbAsync(async db =>
        {
            var run = await db.Runs.Include(r => r.Steps).SingleAsync(r => r.RevisionId == change.RevisionId);
            run.Status = status;
            if (status == RunStatuses.Running)
                foreach (var step in run.Steps.Where(s => s.Kind == StepKinds.Approval))
                    (step.Decision, step.Status) = ("approved", StepStatuses.Succeeded);
            (await db.Revisions.SingleAsync(r => r.Id == change.RevisionId)).InventoryAppliedAt = null;
            await db.SaveChangesAsync();
        });
        var before = await setup.Server.QueryAsync(db => db.Runs.CountAsync());
        await setup.Server.SyncAsync();
        await setup.Server.SyncAsync();
        Assert.Equal(before, await setup.Server.QueryAsync(db => db.Runs.CountAsync()));
        Assert.NotNull(await setup.Server.QueryAsync(db => db.Revisions.Where(r => r.Id == change.RevisionId).Select(r => r.InventoryAppliedAt).SingleAsync()));
    }

    [Fact]
    public async Task A_change_on_a_revision_whose_inventory_is_not_applied_waits_rather_than_ending_no_change()
    {
        await using var setup = await StartAsync();
        var change = await CommitAsync(setup, new ChangeRequest((await setup.FormAsync()).ResourceId, setup.RemoteHead, Hardware: Hardware(2, 4096)));
        await setup.Server.WithDbAsync(async db =>
        {
            db.Runs.RemoveRange(await db.Runs.Where(r => r.RevisionId == change.RevisionId).ToListAsync());
            (await db.Revisions.SingleAsync(r => r.Id == change.RevisionId)).InventoryAppliedAt = null;
            var record = await db.Changes.SingleAsync(c => c.Id == change.Id);
            (record.Status, record.RevisionId, record.RunIds) = (ChangeStatuses.Committed, null, []);
            await db.SaveChangesAsync();
        });

        var waiting = await setup.GetAsync(change.Id);
        Assert.NotEqual(ChangeStatuses.NoChange, waiting.Status);
        Assert.False(waiting.Final);
        await setup.Server.SyncAsync();
        var followed = await setup.GetAsync(change.Id);
        Assert.Equal(ChangeStatuses.Planning, followed.Status);
        Assert.Single(followed.Runs);
    }

    [Fact]
    public async Task A_head_fetched_before_a_push_does_not_make_that_change_unknown()
    {
        await using var setup = await StartAsync();
        var before = setup.RemoteHead;
        var mirror = setup.Server.Services.GetRequiredService<RepositoryMirror>();
        // A sync picks what to check, then fetches the older head; meanwhile this change is pushed and recorded.
        var watched = await ChangeReconciler.SelectAsync(setup.Server.Engine, 100, CancellationToken.None);
        var change = await CommitAsync(setup, new ChangeRequest((await setup.FormAsync()).ResourceId, before, Hardware: Hardware(2, 4096)));
        Assert.DoesNotContain(watched, w => w.Id == change.Id);
        await ChangeReconciler.ReconcileAsync(setup.Server.Engine, mirror, "main", watched, before, TimeProvider.System, CancellationToken.None);
        Assert.Equal(ChangeStatuses.Planning, (await setup.GetAsync(change.Id)).Status);
    }

    [Fact]
    public async Task A_change_on_an_unapplied_revision_moves_on_to_the_applied_one_after_it()
    {
        await using var setup = await StartAsync();
        var change = await CommitAsync(setup, new ChangeRequest((await setup.FormAsync()).ResourceId, setup.RemoteHead, Hardware: Hardware(2, 4096)));
        // What a crash left before revisions were applied in one transaction: the change's revision recorded, not applied.
        await setup.Server.WithDbAsync(async db =>
        {
            db.Runs.RemoveRange(await db.Runs.Where(r => r.RevisionId == change.RevisionId).ToListAsync());
            (await db.Revisions.SingleAsync(r => r.Id == change.RevisionId)).InventoryAppliedAt = null;
            var record = await db.Changes.SingleAsync(c => c.Id == change.Id);
            (record.Status, record.RevisionId, record.RunIds) = (ChangeStatuses.Committed, null, []);
            await db.SaveChangesAsync();
        });
        Assert.Equal(ChangeStatuses.Synced, (await setup.GetAsync(change.Id)).Status);

        // The head moves on before anything recovers that revision; it is never applied itself.
        setup.PushElsewhere(GuestFile, setup.RemoteFile(GuestFile).TrimEnd('\n') + "\ndescription: metadata only");
        for (var i = 0; i < 3; i++)
            await setup.Server.SyncAsync();
        var head = await setup.Server.QueryAsync(db => RepositoryTrust.HeadRevisions(db).AsNoTracking().SingleAsync());
        var followed = await setup.GetAsync(change.Id);
        Assert.Equal(ChangeStatuses.Planning, followed.Status);
        Assert.Equal(head.Id, followed.RevisionId);
        Assert.Equal((await setup.Server.LatestRunAsync(Guest, Operations.Provision)).Id, Assert.Single(followed.Runs).Id);
        Assert.Equal(head.Id, (await setup.Server.LatestRunAsync(Guest, Operations.Provision)).RevisionId);
        Assert.Null(await setup.Server.QueryAsync(db => db.Revisions.Where(r => r.Id == change.RevisionId).Select(r => r.InventoryAppliedAt).SingleAsync()));
    }

    [Fact]
    public async Task Rolling_back_to_a_commit_is_new_work_that_its_earlier_run_does_not_stand_in_for()
    {
        await using var setup = await StartAsync();
        var firstHead = setup.RemoteHead;
        var first = await setup.Server.QueryAsync(db => RepositoryTrust.HeadRevisions(db).AsNoTracking().SingleAsync());
        await CommitAsync(setup, new ChangeRequest((await setup.FormAsync()).ResourceId, firstHead, Hardware: Hardware(2, 4096)));
        setup.MoveMain(firstHead);
        await setup.Server.SyncAsync();
        // The same commit's revision is reused for the rollback, and holds both its first run, cancelled, and the rollback's.
        var both = await setup.Server.QueryAsync(db => db.Runs.AsNoTracking().Where(r => r.RevisionId == first.Id && r.Operation == Operations.Provision).ToListAsync());
        Assert.Equal(2, both.Count);
        await setup.Server.WithDbAsync(async db =>
        {
            db.Runs.RemoveRange(await db.Runs.Where(r => r.RevisionId == first.Id && r.Status != RunStatuses.Cancelled).ToListAsync());
            (await db.Revisions.SingleAsync(r => r.Id == first.Id)).InventoryAppliedAt = null;
            await db.SaveChangesAsync();
        });

        await setup.Server.SyncAsync();
        var runs = await setup.Server.QueryAsync(db => db.Runs.AsNoTracking().Where(r => r.RevisionId == first.Id && r.Operation == Operations.Provision).ToListAsync());
        Assert.Contains(runs, r => !RunStatuses.IsTerminal(r.Status));
    }

    [Fact]
    public async Task A_second_removal_is_its_own_work_even_when_the_clock_went_back()
    {
        var clock = new TestClock { Now = DateTimeOffset.UtcNow };
        await using var setup = await StartAsync(clock: clock);
        var definition = setup.RemoteFile(GuestFile);
        setup.DeleteElsewhere(Folder, "Remove the guest");
        await setup.Server.SyncAsync();
        setup.PushElsewhere(GuestFile, definition.TrimEnd('\n'));
        await setup.Server.SyncAsync();
        clock.Now -= TimeSpan.FromMinutes(5);
        setup.DeleteElsewhere(Folder, "Remove the guest again");
        await setup.Server.SyncAsync();
        var head = await setup.Server.QueryAsync(db => RepositoryTrust.HeadRevisions(db).AsNoTracking().SingleAsync());
        await setup.Server.WithDbAsync(async db =>
        {
            db.Runs.RemoveRange(await db.Runs.Where(r => r.Operation == Operations.Retire && r.Status != RunStatuses.Cancelled).ToListAsync());
            (await db.Revisions.SingleAsync(r => r.Id == head.Id)).InventoryAppliedAt = null;
            await db.SaveChangesAsync();
        });

        await setup.Server.SyncAsync();
        var retires = await setup.Server.QueryAsync(db => db.Runs.AsNoTracking().Where(r => r.Operation == Operations.Retire).ToListAsync());
        Assert.Contains(retires, r => !RunStatuses.IsTerminal(r.Status) && RunContext.Parse(r.Context).DefinitionRemovedIn == head.Commit);
    }

    /// <summary>Takes the database down to the migration before occurrences and up again, as an upgrade from it would.</summary>
    private static async Task ReapplyOccurrencesMigrationAsync(OctopushyDb db)
    {
        await db.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("20261104142039_ChangeChecks");
        await db.Database.MigrateAsync();
    }

    [Fact]
    public async Task Manual_plan_must_certify_recovered_work_even_when_cancelled()
    {
        await using var setup = await StartAsync();
        var change = await CommitAsync(setup, new ChangeRequest((await setup.FormAsync()).ResourceId, setup.RemoteHead, Hardware: Hardware(2, 4096)));
        await setup.Server.WithDbAsync(async db =>
        {
            db.Runs.RemoveRange(await db.Runs.Where(r => r.RevisionId == change.RevisionId).ToListAsync());
            (await db.Revisions.SingleAsync(r => r.Id == change.RevisionId)).InventoryAppliedAt = null;
            await db.SaveChangesAsync();
        });
        var resource = await setup.Server.ResourceAsync(Guest);
        // A person plans the recovered work, then cancels it on purpose.
        var manual = await setup.Server.Services.GetRequiredService<Planner>().PlanGuestAsync(resource.Id, CancellationToken.None);
        Assert.Equal(resource.DesiredOccurrence, (await setup.Server.QueryAsync(db => db.Runs.AsNoTracking().SingleAsync(r => r.Id == manual))).Occurrence);
        await setup.Server.Engine.CancelAsync(manual, "Cancelled by a person", CancellationToken.None);
        var before = await setup.Server.QueryAsync(db => db.Runs.CountAsync(r => r.ResourceId == resource.Id));

        await setup.Server.SyncAsync();
        Assert.Equal(before, await setup.Server.QueryAsync(db => db.Runs.CountAsync(r => r.ResourceId == resource.Id)));
    }

    [Fact]
    public async Task Removing_after_an_invalid_restore_must_get_a_new_occurrence()
    {
        var holder = new FakeAgentHolder();
        await using var setup = await StartAsync(agent: holder);
        await ProvisionAsync(setup, holder.Agent!);
        var definition = setup.RemoteFile(GuestFile);
        setup.DeleteElsewhere(Folder, "Remove the guest");
        await setup.Server.SyncAsync();
        var first = await setup.Server.ResourceAsync(Guest);
        var retire = await setup.Server.LatestRunAsync(Guest, Operations.Retire);
        // Restored with a definition that is invalid, so nothing is provisioned, then removed again.
        setup.PushElsewhere(GuestFile, definition.Replace("cores: 2", "cores: 0").TrimEnd('\n'));
        await setup.Server.SyncAsync();
        var restored = await setup.Server.ResourceAsync(Guest);
        Assert.Null(restored.MissingSince);
        Assert.False(restored.Valid);
        Assert.Equal(RunStatuses.Cancelled, (await setup.Server.QueryAsync(db => db.Runs.AsNoTracking().SingleAsync(r => r.Id == retire.Id))).Status);
        setup.DeleteElsewhere(Folder, "Remove the guest again");
        await setup.Server.SyncAsync();

        var second = await setup.Server.ResourceAsync(Guest);
        var retires = await setup.Server.QueryAsync(db => db.Runs.AsNoTracking().Where(r => r.ResourceId == second.Id && r.Operation == Operations.Retire).ToListAsync());
        Assert.Contains(retires, r => !RunStatuses.IsTerminal(r.Status));
        Assert.NotEqual(first.DesiredOccurrence, second.DesiredOccurrence);
    }

    [Fact]
    public async Task Occurrence_backfill_must_not_certify_a_rollback_with_its_historical_run()
    {
        await using var setup = await StartAsync();
        var firstHead = setup.RemoteHead;
        var first = await setup.Server.QueryAsync(db => RepositoryTrust.HeadRevisions(db).AsNoTracking().SingleAsync());
        await CommitAsync(setup, new ChangeRequest((await setup.FormAsync()).ResourceId, firstHead, Hardware: Hardware(2, 4096)));
        setup.MoveMain(firstHead);
        await setup.Server.SyncAsync();
        await setup.Server.WithDbAsync(async db =>
        {
            db.Runs.RemoveRange(await db.Runs.Where(r => r.RevisionId == first.Id && r.Status != RunStatuses.Cancelled).ToListAsync());
            (await db.Revisions.SingleAsync(r => r.Id == first.Id)).InventoryAppliedAt = null;
            await db.SaveChangesAsync();
            await ReapplyOccurrencesMigrationAsync(db);
        });

        await setup.Server.SyncAsync();
        var runs = await setup.Server.QueryAsync(db => db.Runs.AsNoTracking().Where(r => r.Operation == Operations.Provision).ToListAsync());
        Assert.Contains(runs, r => r.RevisionId == first.Id && !RunStatuses.IsTerminal(r.Status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_current_work_is_backfilled_and_not_reproposed(bool removal)
    {
        await using var setup = await StartAsync();
        if (removal)
        {
            setup.DeleteElsewhere(Folder, "Remove the guest");
            await setup.Server.SyncAsync();
        }

        var before = await setup.Server.QueryAsync(db => db.Runs.CountAsync());
        await setup.Server.WithDbAsync(async db =>
        {
            await ReapplyOccurrencesMigrationAsync(db);
            (await RepositoryTrust.HeadRevisions(db).SingleAsync()).InventoryAppliedAt = null;
            await db.SaveChangesAsync();
        });
        var resource = await setup.Server.ResourceAsync(Guest);
        var witness = await setup.Server.QueryAsync(db => db.Runs.AsNoTracking().SingleAsync(r => r.ResourceId == resource.Id && r.Occurrence == resource.DesiredOccurrence));
        Assert.Equal(removal ? Operations.Retire : Operations.Provision, witness.Operation);
        await setup.Server.SyncAsync();
        Assert.Equal(before, await setup.Server.QueryAsync(db => db.Runs.CountAsync()));
    }

    [Fact]
    public async Task Backfilled_work_with_no_matching_run_is_recovered()
    {
        await using var setup = await StartAsync();
        var change = await CommitAsync(setup, new ChangeRequest((await setup.FormAsync()).ResourceId, setup.RemoteHead, Hardware: Hardware(2, 4096)));
        await setup.Server.WithDbAsync(async db =>
        {
            db.Runs.RemoveRange(await db.Runs.Where(r => r.RevisionId == change.RevisionId).ToListAsync());
            (await db.Revisions.SingleAsync(r => r.Id == change.RevisionId)).InventoryAppliedAt = null;
            await db.SaveChangesAsync();
            await ReapplyOccurrencesMigrationAsync(db);
        });
        var resource = await setup.Server.ResourceAsync(Guest);
        Assert.False(await setup.Server.QueryAsync(db => db.Runs.AnyAsync(r => r.Occurrence == resource.DesiredOccurrence)));
        await setup.Server.SyncAsync();
        Assert.True(await setup.Server.QueryAsync(db => db.Runs.AnyAsync(r => r.Occurrence == resource.DesiredOccurrence && r.Status != RunStatuses.Cancelled)));
    }

    [Fact]
    public async Task An_applied_successor_must_not_hide_the_unapplied_revisions_retained_run()
    {
        await using var setup = await StartAsync();
        var change = await CommitAsync(setup, new ChangeRequest((await setup.FormAsync()).ResourceId, setup.RemoteHead, Hardware: Hardware(2, 4096)));
        // The change's revision unapplied, with its run still queued; then a commit that changes nothing for the guest.
        await setup.Server.WithDbAsync(async db =>
        {
            (await db.Revisions.SingleAsync(r => r.Id == change.RevisionId)).InventoryAppliedAt = null;
            await db.SaveChangesAsync();
        });
        setup.PushElsewhere(GuestFile, setup.RemoteFile(GuestFile).TrimEnd('\n') + "\ndescription: metadata only");
        await setup.Server.SyncAsync();

        var after = await setup.GetAsync(change.Id);
        Assert.Equal(ChangeStatuses.Planning, after.Status);
        Assert.False(after.Final);
        Assert.Single(after.Runs);
    }

    [Fact]
    public async Task A_later_revisions_error_is_not_the_earlier_changes_failure()
    {
        await using var setup = await StartAsync();
        var form = await setup.FormAsync();
        // A pin to the version it already resolves to proposes no run.
        setup.PushElsewhere(GuestFile, Commented.Replace("workflow: test-vm@1", "workflow: test-vm@1.0").TrimEnd('\n'));
        var pinned = setup.RemoteHead;
        var id = Guid.CreateVersion7();
        await setup.Server.WithDbAsync(async db =>
        {
            db.Changes.Add(new ResourceChange
            {
                Id = id, Kind = ChangeKinds.Edit, ResourceKind = ResourceKinds.Guest, ResourceId = form.ResourceId, Address = Guest, Author = "ops",
                BaseCommit = form.BaseCommit!, Message = "krb-app-01: workflow test-vm@1 → test-vm@1.0", Status = ChangeStatuses.Committed,
                Files = [GuestFile], Commit = pinned, Edited = """{"workflow":"test-vm@1.0"}""",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        await setup.Server.SyncAsync();
        // A later commit breaks the guest, before anyone looked at the change.
        setup.PushElsewhere(GuestFile, Commented.Replace("workflow: test-vm@1", "workflow: test-vm@1.0").Replace("cores: 2", "cores: 0").TrimEnd('\n'));
        await setup.Server.SyncAsync();
        Assert.False((await setup.Server.ResourceAsync(Guest)).Valid);

        var change = (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{id}"))!;
        Assert.Equal(ChangeStatuses.NoChange, change.Status);
    }

    [Fact]
    public async Task A_stale_form_cannot_write_under_an_installation_that_became_observed_only()
    {
        await using var setup = await StartAsync();
        var form = await setup.FormAsync();
        const string installation = "labs/home/proxmox/korriban/installation.yaml";
        var managed = setup.RemoteFile(installation);
        setup.PushElsewhere(installation, managed.Replace("type: standalone", "manage: false\ntype: standalone").TrimEnd('\n'));
        var head = setup.RemoteHead;

        // The definition it would be merged with says observed only, though the form's commit does not.
        var refused = await RefusedAsync(setup, new ChangeRequest(form.ResourceId, form.BaseCommit!, Hardware: Hardware(2, 4096)));
        Assert.Equal(ChangeRefusal.Reasons.ObservedOnly, refused.Reason);
        Assert.Equal(head, setup.RemoteHead);

        // Octopushy's own record says observed only, though the definition it would be merged with cannot be read.
        await setup.Server.SyncAsync();
        Assert.True((await setup.Server.ResourceAsync("home/korriban")).ObservedOnly);
        setup.PushElsewhere(installation, "schemaVersion: 1\ntype: standalone");
        head = setup.RemoteHead;
        refused = await RefusedAsync(setup, new ChangeRequest(form.ResourceId, form.BaseCommit!, Hardware: Hardware(2, 4096)));
        Assert.Equal(ChangeRefusal.Reasons.ObservedOnly, refused.Reason);
        Assert.Equal(head, setup.RemoteHead);
    }

    [Fact]
    public async Task A_removal_whose_retire_run_cannot_be_planned_fails_with_why()
    {
        await using var setup = await StartAsync();
        setup.PushElsewhere(GuestFile, Commented.Replace("cores: 2", "cores: 0"));
        await setup.Server.SyncAsync();
        Assert.False((await setup.Server.ResourceAsync(Guest)).Valid);
        var form = await setup.FormAsync();

        var change = await CommitAsync(setup, new ChangeRequest(form.ResourceId, form.BaseCommit!, Kind: ChangeKinds.Remove));
        Assert.Equal(ChangeStatuses.Failed, change.Status);
        Assert.Contains("its removal cannot be planned", change.Detail);
        Assert.Empty(change.Runs);
    }

    [Fact]
    public async Task With_publishing_off_the_form_is_read_only_and_nothing_is_committed()
    {
        await using var setup = await StartAsync(publish: false);
        var form = await setup.FormAsync();
        Assert.Contains("Octopushy__RepositoryEditor__PublishEnabled=true", form.BlockedReason);
        Assert.Contains("deploy key with write access", form.BlockedReason);
        Assert.NotNull(form.Hardware);
        var refused = await RefusedAsync(setup, new ChangeRequest(form.ResourceId, form.BaseCommit!, Hardware: Hardware(2, 4096)));
        Assert.Equal(ChangeRefusal.Reasons.Disabled, refused.Reason);
        Assert.Equal(form.BaseCommit, setup.RemoteHead);
    }

    [Fact]
    public async Task A_push_that_fails_is_refused_with_what_the_credential_needs()
    {
        await using var setup = await StartAsync();
        var form = await setup.FormAsync();
        await setup.Server.WithDbAsync(async db =>
        {
            (await db.RepositorySettings.SingleAsync()).CredentialKind = "token";
            await db.SaveChangesAsync();
        });
        var refused = await RefusedAsync(setup, new ChangeRequest(form.ResourceId, form.BaseCommit!, Hardware: Hardware(2, 4096)));
        Assert.Equal(ChangeRefusal.Reasons.PushFailed, refused.Reason);
        Assert.Contains("The configured repository credential is missing.", refused.Message);
        Assert.Contains("deploy key with write access", refused.Message);
        Assert.Equal(form.BaseCommit, setup.RemoteHead);
    }

    [Fact]
    public async Task Nothing_under_an_observed_only_installation_can_be_changed()
    {
        var lab = Lab();
        lab.Write("labs/home/proxmox/korriban/installation.yaml",
            File.ReadAllText(Path.Combine(lab.Root, "labs/home/proxmox/korriban/installation.yaml")).Replace("type: standalone", "manage: false\ntype: standalone"));
        await using var setup = await StartAsync(lab);
        var form = await setup.FormAsync();
        Assert.Equal("home/korriban is observed only (manage: false): Octopushy changes nothing there, so nothing under it can be changed here.", form.BlockedReason);
        var refused = await RefusedAsync(setup, new ChangeRequest(form.ResourceId, setup.RemoteHead, Hardware: Hardware(2, 4096)));
        Assert.Equal(ChangeRefusal.Reasons.ObservedOnly, refused.Reason);
        Assert.Equal(form.BlockedReason, refused.Message);
    }

    [Fact]
    public async Task A_services_inputs_are_edited_in_its_own_file()
    {
        var lab = ServiceDefinitionTests.Repository(ServiceDefinitionTests.TofuOnly);
        await using var setup = await StartAsync(lab);
        var form = await setup.FormAsync(ServiceDefinitionTests.Guest + "/web");
        Assert.Equal(ResourceKinds.Service, form.ResourceKind);
        Assert.Null(form.Hardware);
        Assert.False(form.CanRemove);
        var change = await CommitAsync(setup, new ChangeRequest(form.ResourceId, form.BaseCommit!, Inputs: new JsonObject { ["port"] = 8443 }));
        Assert.Equal("web: port 8080 → 8443", change.Message);
        Assert.Equal(ServiceDefinitionTests.TofuOnly.Replace("8080", "8443") + "\n", setup.RemoteFile(ServiceDefinitionTests.ServiceFile));
        Assert.Equal([ServiceDefinitionTests.ServiceFile], change.Files);
        var hardware = await RefusedAsync(setup, new ChangeRequest(form.ResourceId, change.Commit!, Hardware: Hardware(1, 512)));
        Assert.Equal(ChangeRefusal.Reasons.Invalid, hardware.Reason);
    }

    [Fact]
    public async Task An_upgrade_names_the_next_major_and_its_new_inputs_in_the_same_commit()
    {
        using var workflows = TestWorkflows.Create();
        workflows.Files.Write("workflows/test-vm/workflow.yaml", """
            schemaVersion: 1
            version: "2.0"
            manages: vm
            description: A test VM that needs a tier.
            inputs: inputs.schema.json
            stages:
              - name: machine
                executor: opentofu
                root: ../../modules/opentofu/test-vm
                verify: proxmox
            """);
        workflows.Files.Write("workflows/test-vm/inputs.schema.json", """
            { "type": "object", "properties": { "inputs": { "type": "object",
              "properties": { "tier": { "enum": ["bronze", "gold"] } }, "required": ["tier"] } } }
            """);
        workflows.Commit("test-vm 2.0");
        workflows.Tag("test-vm/v2.0");
        var lab = workflows.Config(Lab());
        await using var setup = await StartAsync(lab);

        var catalog = (await setup.Client.GetFromJsonAsync<WorkflowCatalogDto>("/api/workflows"))!;
        Assert.Equal("repository", catalog.Source);
        var testVm = Assert.Single(catalog.Workflows);
        Assert.Equal(["2.0", "1.0"], testVm.Versions.Select(v => v.Version));
        Assert.Equal("gold", testVm.Versions[0].Inputs!["properties"]!["inputs"]!["properties"]!["tier"]!["enum"]![1]!.GetValue<string>());

        var form = await setup.FormAsync();
        Assert.Equal("1.0", form.Resolved!.Version);
        Assert.Equal("2.0", form.Upgrade!.Version);
        var upgrade = new ChangeRequest(form.ResourceId, form.BaseCommit!, Kind: ChangeKinds.Upgrade, Workflow: "test-vm@2");
        var preview = (await (await setup.Client.PostAsJsonAsync("/api/changes/preview", upgrade)).Content.ReadFromJsonAsync<ChangePreviewDto>())!;
        Assert.False(preview.Valid);
        Assert.Contains(preview.FieldIssues, i => i.Pointer == "/inputs");

        var change = await CommitAsync(setup, upgrade with { Inputs = new JsonObject { ["tier"] = "gold" } });
        Assert.Equal(ChangeKinds.Upgrade, change.Kind);
        Assert.Equal("krb-app-01: workflow test-vm@1 → test-vm@2, inputs set to {tier: gold}", change.Message);
        var file = setup.RemoteFile(GuestFile);
        Assert.Contains("# Picked by hand.\nworkflow: test-vm@2\ninputs:\n  tier: gold\n", file);
        Assert.Equal(ChangeStatuses.Planning, change.Status);
        Assert.Equal("test-vm@2.0", (await setup.Server.ResourceAsync(Guest)).Workflow);
    }
}
