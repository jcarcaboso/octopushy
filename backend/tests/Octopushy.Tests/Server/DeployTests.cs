using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Changes;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>
/// The workflow wizard's deploys: a new guest's or service's folder as one commit, the values it generates, and the plan it
/// may approve in the same click. See docs/changes-from-the-ui.md.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DeployTests(PostgresFixture postgres)
{
    private const string Installation = "home/korriban";
    private const string Existing = "home/korriban/krb-app-01";
    private const string NewGuest = "home/korriban/new-01";
    private const string NewFile = "labs/home/proxmox/korriban/guests/new-01/guest.yaml";

    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "hardware": {
              "type": "object",
              "properties": {
                "cores": { "type": "integer", "minimum": 1, "default": 2 },
                "memory": { "description": "MiB", "type": "integer", "minimum": 256 }
              },
              "required": ["cores", "memory"],
              "additionalProperties": false
            },
            "network": { "type": "object" },
            "inputs": {
              "type": "object",
              "properties": { "channel": { "type": "string", "enum": ["stable", "beta"] } }
            }
          }
        }
        """;

    /// <summary>
    /// The minimal lab with a VM workflow, one whose setup takes an enrollment token it generates, a service workflow, and an
    /// installation that is observed only. With <paramref name="guests"/> false, the lab's one guest is left out.
    /// </summary>
    private static TestRepository Lab(string policy = "", bool guests = true)
    {
        var repository = TestRepository.Minimal();
        if (!guests)
            repository.Delete("labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml");
        repository.Write("labs/home/lab.yaml", $"schemaVersion: 1\nname: Home\nexecution: {{ agentLabels: [home] }}\n{policy}\n");
        repository.Write("workflows/test-vm/workflow.yaml", """
            schemaVersion: 1
            version: "1.0"
            manages: vm
            description: A test VM.
            inputs: inputs.schema.json
            ui:
              vmid: { generate: next-vmid }
            stages:
              - name: machine
                executor: opentofu
                root: ../../modules/opentofu/test-vm
                verify: proxmox
            """);
        repository.Write("workflows/test-vm/inputs.schema.json", Schema);
        repository.Write("workflows/agent-vm/workflow.yaml", """
            schemaVersion: 1
            version: "1.1"
            manages: vm
            description: A VM that enrolls an agent.
            secrets: [enroll_token]
            ui:
              secrets:
                enroll_token: { generate: agent-enrollment-token, labels: inputs.labels }
                admin_password: { generate: random-password, length: 24 }
            stages:
              - name: machine
                executor: opentofu
                root: ../../modules/opentofu/test-vm
                verify: proxmox
              - name: setup
                executor: ansible
                root: ../../modules/ansible/agent
                playbook: site.yml
            """);
        repository.Write("modules/ansible/agent/site.yml", "- hosts: all\n  tasks: []\n");
        repository.Write("workflows/rules/workflow.yaml", """
            schemaVersion: 1
            version: "2.0"
            manages: service
            description: Firewall rules.
            stages:
              - { name: expose, executor: opentofu, root: ../../modules/opentofu/rules }
            """);
        repository.Write("modules/opentofu/rules/main.tf", "variable \"port\" {\n  type    = number\n  default = 80\n}\n\nresource \"terraform_data\" \"rule\" {\n  input = var.port\n}\n");
        repository.Write("labs/home/proxmox/watch/installation.yaml", """
            schemaVersion: 1
            manage: false
            type: standalone
            connection:
              endpoint: https://10.9.0.100:8006/
              tls: { verify: false }
              token: { store: proxmox/watch/token }
            hosts:
              watch: {}
            """);
        return repository;
    }

    private sealed class Setup : IAsyncDisposable
    {
        public required ServerHarness Server { get; init; }
        public required HttpClient Client { get; init; }
        public required TestRepository Remote { get; init; }
        public FakeAgent? Agent { get; init; }

        public string RemoteHead => TestWorkflows.Run(Remote.Root, "rev-parse", "main").Trim();

        public string RemoteFile(string path) => TestWorkflows.Run(Remote.Root, "show", $"main:{path}");

        public void PushElsewhere(string path, string content)
        {
            using var work = new TestRepository();
            TestWorkflows.Run(work.Root, "clone", "--quiet", Remote.Root, ".");
            work.Write(path, content);
            TestWorkflows.Run(work.Root, "add", "-A");
            TestWorkflows.Run(work.Root, "-c", "user.name=Other", "-c", "user.email=other@example.invalid", "commit", "--quiet", "-m", $"Add {path}");
            TestWorkflows.Run(work.Root, "push", "--quiet", "origin", "HEAD:main");
        }

        public async Task<DeployTargetsDto> TargetsAsync() => (await Client.GetFromJsonAsync<DeployTargetsDto>("/api/deploy/targets"))!;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
            Remote.Dispose();
        }
    }

    private async Task<Setup> StartAsync(TestRepository? lab = null, bool agent = false)
    {
        var server = await ServerHarness.StartAsync(postgres, lab ?? Lab(),
            settings: new() { ["Octopushy:RepositoryEditor:PublishEnabled"] = "true" });
        var remote = new TestRepository();
        await server.Services.GetRequiredService<GitCli>().RunAsync(["clone", "--bare", "--quiet", server.Repository.Root, remote.Root]);
        await server.WithDbAsync(async db =>
        {
            (await db.RepositorySettings.SingleAsync()).Url = remote.Root;
            await db.SaveChangesAsync();
        });
        var fake = agent ? await server.ConnectAgentAsync("home") : null;
        var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        return new Setup { Server = server, Client = client, Remote = remote, Agent = fake };
    }

    private static DeployRequest Guest(string baseCommit, int vmid, string name = "new-01", string workflow = "test-vm@1", string parent = Installation,
        IReadOnlyDictionary<string, SecretChoice>? secrets = null, JsonObject? inputs = null, bool approve = false, int memory = 2048) =>
        new(ChangeKinds.CreateGuest, baseCommit, parent, name, workflow, Host: parent == Installation ? "korriban" : "watch", Vmid: vmid,
            Hardware: new JsonObject { ["cores"] = 2, ["memory"] = memory },
            Network: new JsonObject { ["bridge"] = "vmbr0", ["mode"] = "static", ["address"] = "10.1.0.40/24", ["gateway"] = "10.1.0.1" },
            Inputs: inputs, Secrets: secrets, ApproveAtDeploy: approve);

    private static async Task<ChangeDto> DeployAsync(Setup setup, DeployRequest request)
    {
        var response = await setup.Client.PostAsJsonAsync("/api/deploy", request);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ChangeDto>())!;
    }

    private static async Task<ChangeRefusedDto> RefusedAsync(Setup setup, DeployRequest request, string path = "/api/deploy")
    {
        var response = await setup.Client.PostAsJsonAsync(path, request);
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ChangeRefusedDto>())!;
    }

    private static async Task<ChangePreviewDto> PreviewAsync(Setup setup, DeployRequest request)
    {
        var response = await setup.Client.PostAsJsonAsync("/api/deploy/preview", request);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ChangePreviewDto>())!;
    }

    [Fact]
    public async Task A_new_guest_is_one_commit_of_a_commented_guest_yaml_in_its_own_folder()
    {
        await using var setup = await StartAsync();
        var targets = await setup.TargetsAsync();
        Assert.Null(targets.BlockedReason);
        Assert.Equal(setup.RemoteHead, targets.BaseCommit);
        var korriban = targets.Installations.Single(i => i.Address == Installation);
        Assert.True(korriban.Managed);
        Assert.Equal(["korriban"], korriban.Hosts.Select(h => h.Name));
        Assert.Equal(["home"], korriban.AgentLabels);

        var request = Guest(targets.BaseCommit!, 311, inputs: new JsonObject { ["channel"] = "stable" });
        var preview = await PreviewAsync(setup, request);
        Assert.True(preview.Valid, string.Join("; ", preview.Issues.Select(i => i.Message)));
        Assert.Null(preview.Refused);
        Assert.Equal([NewFile], preview.Files);
        Assert.Equal("new-01: deploy test-vm@1 on home/korriban, host korriban, VMID 311", preview.Subject);
        Assert.Contains($"+++ b/{NewFile}", preview.Diff);

        var change = await DeployAsync(setup, request);
        Assert.Equal(ChangeKinds.CreateGuest, change.Kind);
        Assert.Equal(NewGuest, change.Address);
        Assert.Equal(311, change.Vmid);
        Assert.Equal(setup.RemoteHead, change.Commit);
        Assert.Equal("""
            # new-01: deployed from test-vm@1.0 with Octopushy's workflow wizard.
            # A test VM.
            schemaVersion: 1
            type: vm
            host: korriban
            vmid: 311                         # free on home/korriban when deployed
            workflow: test-vm@1               # the newest 1.x; test-vm@1.0 pins this one
            hardware:
              cores: 2
              memory: 2048
            network:
              bridge: vmbr0
              mode: static
              address: 10.1.0.40/24
              gateway: 10.1.0.1
            inputs:
              channel: stable

            """.ReplaceLineEndings("\n"), setup.RemoteFile(NewFile));
        var log = TestWorkflows.Run(setup.Remote.Root, "log", "-1", "--format=%an|%B", "main").Trim();
        Assert.Equal($"ops|new-01: deploy test-vm@1 on home/korriban, host korriban, VMID 311\n\nOctopushy-Change: {change.Id}", log);
        Assert.Equal(["labs/home/proxmox/korriban/guests/new-01/guest.yaml"],
            TestWorkflows.Run(setup.Remote.Root, "diff", "--name-only", $"{targets.BaseCommit}..main").Trim().Split('\n'));

        // Synced at once: the new guest is a resource, the change follows it, and its first run is proposed.
        var resource = await setup.Server.ResourceAsync(NewGuest);
        Assert.Equal(resource.Id, change.ResourceId);
        var run = await setup.Server.LatestRunAsync(NewGuest, Operations.Provision);
        Assert.Equal([run.Id], change.Runs.Select(r => r.Id));
        Assert.Equal(ChangeStatuses.Planning, change.Status);
    }

    [Fact]
    public async Task A_pinned_reference_is_written_as_chosen()
    {
        await using var setup = await StartAsync();
        var head = (await setup.TargetsAsync()).BaseCommit!;
        await DeployAsync(setup, Guest(head, 311, workflow: "test-vm@1.0"));
        Assert.Contains("workflow: test-vm@1.0             # pinned; test-vm@1 follows the newest 1.x\n", setup.RemoteFile(NewFile));
    }

    [Theory]
    [InlineData("krb-app-01", Installation, "exists", "labs/home/proxmox/korriban/guests/krb-app-01 is already in the repository")]
    [InlineData("Bad_Name", Installation, "invalid", "'Bad_Name' is not a valid name")]
    [InlineData("-new", Installation, "invalid", "is not a valid name")]
    [InlineData("new-01", "home/watch", "observed-only", "home/watch is observed only (manage: false): Octopushy changes nothing there, so nothing can be deployed to it.")]
    [InlineData("new-01", "home/nowhere", "unavailable", "There is no installation home/nowhere")]
    public async Task A_deploy_is_refused_on_an_existing_folder_a_bad_name_or_an_observed_only_installation(string name, string parent, string reason,
        string message)
    {
        await using var setup = await StartAsync();
        var head = setup.RemoteHead;
        var refused = await RefusedAsync(setup, Guest(head, 400, name: name, parent: parent));
        Assert.Equal(reason, refused.Reason);
        Assert.Contains(message, refused.Message);
        Assert.Equal(head, setup.RemoteHead);
        Assert.Equal(reason, (await RefusedAsync(setup, Guest(head, 400, name: name, parent: parent), "/api/deploy/preview")).Reason);
    }

    [Fact]
    public async Task An_observed_only_installation_is_listed_but_cannot_be_deployed_to()
    {
        await using var setup = await StartAsync();
        var watch = (await setup.TargetsAsync()).Installations.Single(i => i.Address == "home/watch");
        Assert.False(watch.Managed);
        Assert.Equal("home/watch is observed only (manage: false): Octopushy changes nothing there, so nothing can be deployed to it.", watch.Reason);
    }

    [Fact]
    public async Task The_next_free_vmid_skips_observed_declared_and_in_flight_vmids()
    {
        await using var setup = await StartAsync();
        async Task<int> NextAsync(int from) =>
            (await setup.Client.GetFromJsonAsync<NextVmidDto>($"/api/deploy/vmid?installation={Installation}&from={from}"))!.Vmid;

        Assert.Equal(100, await NextAsync(0));
        Assert.Equal(311, await NextAsync(310));
        await setup.Server.WithDbAsync(async db =>
        {
            var installation = await db.Resources.FirstAsync(r => r.Address == Installation);
            db.Observations.Add(new Observation
            {
                Id = Guid.CreateVersion7(), ResourceId = installation.Id, ObservedAt = DateTimeOffset.UtcNow,
                Data = new JsonObject
                {
                    ["hosts"] = new JsonArray(new JsonObject { ["name"] = "korriban" }),
                    ["guests"] = new JsonArray(new JsonObject { ["vmid"] = 311, ["name"] = "homeassistant", ["host"] = "korriban" },
                        new JsonObject { ["vmid"] = 100, ["name"] = "template", ["host"] = "korriban", ["template"] = true }),
                }.ToJsonString(),
            });
            // Another deploy that is committed and not settled holds its VMID.
            db.Changes.Add(new ResourceChange
            {
                Id = Guid.CreateVersion7(), Kind = ChangeKinds.CreateGuest, ResourceKind = ResourceKinds.Guest, Address = "home/korriban/other-01",
                Author = "ops", BaseCommit = new string('a', 40), Message = "other-01: deploy", Commit = new string('b', 40), Vmid = 312,
                Status = ChangeStatuses.Committed, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            // One that was refused holds nothing.
            db.Changes.Add(new ResourceChange
            {
                Id = Guid.CreateVersion7(), Kind = ChangeKinds.CreateGuest, ResourceKind = ResourceKinds.Guest, Address = "home/korriban/gone-01",
                Author = "ops", BaseCommit = new string('a', 40), Message = "gone-01: deploy", Vmid = 313,
                Status = ChangeStatuses.Refused, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });

        Assert.Equal(101, await NextAsync(100));
        Assert.Equal(313, await NextAsync(310));
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Client.GetAsync("/api/deploy/vmid?installation=home/nowhere")).StatusCode);

        var refused = await RefusedAsync(setup, Guest(setup.RemoteHead, 311));
        Assert.Equal(ChangeRefusal.Reasons.VmidTaken, refused.Reason);
        Assert.Equal(313, refused.NextVmid);
        Assert.Contains("VMID 311 is not free on home/korriban: observed on korriban as homeassistant", refused.Message);
        Assert.Contains(refused.FieldIssues, i => i.Pointer == "/vmid");
    }

    [Fact]
    public async Task A_vmid_taken_after_the_review_refuses_the_commit_and_offers_the_next()
    {
        await using var setup = await StartAsync();
        var head = setup.RemoteHead;
        var request = Guest(head, 311, workflow: "agent-vm@1",
            secrets: new Dictionary<string, SecretChoice> { ["enroll_token"] = new(Generate: SecretGenerators.AgentEnrollmentToken) });
        var preview = await PreviewAsync(setup, request);
        Assert.Null(preview.Refused);

        // Someone declares 311 on the branch between the review and the deploy.
        setup.PushElsewhere("labs/home/proxmox/korriban/guests/other-01/guest.yaml",
            "schemaVersion: 1\ntype: vm\nhost: korriban\nvmid: 311\nworkflow: test-vm@1\nhardware: { cores: 1, memory: 512 }\nnetwork: { bridge: vmbr0, mode: dhcp }\n");
        var moved = setup.RemoteHead;
        var refused = await RefusedAsync(setup, request);
        Assert.Equal(ChangeRefusal.Reasons.VmidTaken, refused.Reason);
        Assert.Contains("declared by home/korriban/other-01", refused.Message);
        Assert.Equal(312, refused.NextVmid);
        Assert.Equal(moved, setup.RemoteHead);

        // Nothing generated is left behind: no store value, no enrollment token.
        Assert.False(await setup.Server.QueryAsync(db => db.StoreEntries.AnyAsync(e => e.Key.StartsWith("guests/"))));
        Assert.Equal(0, await setup.Server.QueryAsync(db => db.EnrollmentTokens.CountAsync()));
        var recorded = await setup.Server.QueryAsync(db => db.Changes.SingleAsync(c => c.Id == refused.ChangeId));
        Assert.Equal(ChangeStatuses.Refused, recorded.Status);
        Assert.Empty(recorded.GeneratedKeys);
    }

    [Fact]
    public async Task A_generated_enrollment_token_is_stored_and_referenced_never_written()
    {
        await using var setup = await StartAsync();
        var request = Guest(setup.RemoteHead, 320, workflow: "agent-vm@1",
            secrets: new Dictionary<string, SecretChoice>
            {
                ["enroll_token"] = new(Generate: SecretGenerators.AgentEnrollmentToken, Labels: ["home", "worker"], Name: "worker-new"),
                ["admin_password"] = new(Generate: SecretGenerators.RandomPassword),
            }) with { AccessUser = "ops", HostKey = "guests/home/korriban/new-01/host-key" };
        var preview = await PreviewAsync(setup, request);
        Assert.True(preview.Valid, string.Join("; ", preview.Issues.Select(i => i.Message)));
        const string Key = "guests/home/korriban/new-01/enroll_token";
        Assert.Equal([(Key, SecretGenerators.AgentEnrollmentToken), ("guests/home/korriban/new-01/admin_password", SecretGenerators.RandomPassword)],
            preview.Generated!.Select(g => (g.Key, g.Generator)));
        Assert.Contains("an agent named worker-new, labels [home, worker], valid for 72 hours", preview.Generated![0].Detail);
        // A review generates nothing.
        Assert.Equal(0, await setup.Server.QueryAsync(db => db.EnrollmentTokens.CountAsync()));

        var change = await DeployAsync(setup, request);
        Assert.Equal([Key, "guests/home/korriban/new-01/admin_password"], change.GeneratedKeys);
        var file = setup.RemoteFile(NewFile);
        Assert.Contains("secrets:                          # values are in the variable store, never in Git\n"
            + $"  enroll_token: {{ store: {Key} }}  # generated at deploy: one-time enrollment token\n", file);
        Assert.Contains("  admin_password: { store: guests/home/korriban/new-01/admin_password }  # generated at deploy: random password\n", file);
        Assert.Contains("access:\n  user: ops\n  hostKey: { store: guests/home/korriban/new-01/host-key }  # a plain entry", file);

        var token = await setup.Server.QueryAsync(db => db.EnrollmentTokens.SingleAsync());
        Assert.Equal("worker-new", token.Name);
        Assert.Equal(["home", "worker"], token.Labels);
        Assert.InRange(token.ExpiresAt - token.CreatedAt, TimeSpan.FromHours(71), TimeSpan.FromHours(73));
        var (value, version) = (await setup.Server.Services.CreateAsyncScope().ServiceProvider.GetRequiredService<Octopushy.Server.Store.VariableStore>()
            .GetAsync(Key, CancellationToken.None))!.Value;
        Assert.Equal(1, version);
        Assert.StartsWith("opy1.", value);
        Assert.Equal(token.TokenHash, Octopushy.Server.Agents.EnrollmentService.TokenHash(value.Split('.')[1]));
        var password = (await setup.Server.QueryAsync(db => db.StoreEntries.SingleAsync(e => e.Key == "guests/home/korriban/new-01/admin_password")));
        Assert.Equal(Visibilities.Secret, password.Visibility);

        // The value is nowhere in Git, and not in what the API returns.
        var history = TestWorkflows.Run(setup.Remote.Root, "log", "-p", "--all");
        Assert.DoesNotContain(value, history);
        Assert.DoesNotContain(value, await (await setup.Client.GetAsync($"/api/changes/{change.Id}")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_generated_value_is_deleted_and_its_token_revoked_when_the_commit_is_refused()
    {
        await using var setup = await StartAsync();
        // A workflow with a setup and a secret, refused once the token exists: its inputs fail the candidate's validation.
        var head = setup.RemoteHead;
        var refused = await RefusedAsync(setup, Guest(head, 320, workflow: "agent-vm@1",
            secrets: new Dictionary<string, SecretChoice> { ["enroll_token"] = new(Generate: SecretGenerators.AgentEnrollmentToken) })
            with { Template = null, Inputs = new JsonObject { ["ansible_host"] = "elsewhere" } });
        Assert.Equal(ChangeRefusal.Reasons.Invalid, refused.Reason);
        Assert.Equal(head, setup.RemoteHead);
        Assert.False(await setup.Server.QueryAsync(db => db.StoreEntries.AnyAsync(e => e.Key.StartsWith("guests/"))));
        Assert.False(await setup.Server.QueryAsync(db => db.StoreVersions.AnyAsync(v => v.Key.StartsWith("guests/"))));
        Assert.Equal(0, await setup.Server.QueryAsync(db => db.EnrollmentTokens.CountAsync()));
    }

    [Fact]
    public async Task A_deploy_whose_request_ended_before_its_push_is_refused_by_reconciliation_and_its_generated_values_discarded()
    {
        await using var setup = await StartAsync();
        // As a request cancelled before its push leaves it: recorded as committing, its token and value made, no candidate commit.
        var id = Guid.CreateVersion7();
        const string Key = "guests/home/korriban/lost-01/enroll_token";
        var token = Guid.Empty;
        await setup.Server.WithDbAsync(async db =>
        {
            var ca = setup.Server.Services.GetRequiredService<Octopushy.Server.Agents.AgentCertificateAuthority>();
            var (made, _) = Octopushy.Server.Agents.EnrollmentTokens.Create(db, ca, "lost-01", ["home"], 2, 60, DateTimeOffset.UtcNow);
            token = made.Id;
            var then = DateTimeOffset.UtcNow.AddMinutes(-1);
            db.Changes.Add(new ResourceChange
            {
                Id = id, Kind = ChangeKinds.CreateGuest, ResourceKind = ResourceKinds.Guest, Address = "home/korriban/lost-01", Author = "ops",
                BaseCommit = setup.RemoteHead, Message = "lost-01: deploy", Status = ChangeStatuses.Committing, Vmid = 330,
                GeneratedKeys = [Key], GeneratedTokens = [made.Id], CreatedAt = then, UpdatedAt = then,
            });
            await db.SaveChangesAsync();
        });
        await setup.Server.SetSecretAsync(Key, "a generated value");

        await setup.Server.SyncAsync();
        var change = await setup.Server.QueryAsync(db => db.Changes.AsNoTracking().SingleAsync(c => c.Id == id));
        Assert.Equal(ChangeStatuses.Refused, change.Status);
        Assert.Empty(change.GeneratedKeys);
        Assert.False(await setup.Server.QueryAsync(db => db.StoreEntries.AnyAsync(e => e.Key == Key)));
        Assert.False(await setup.Server.QueryAsync(db => db.EnrollmentTokens.AnyAsync(t => t.Id == token)));
        // It no longer holds its VMID.
        Assert.Equal(330, (await setup.Client.GetFromJsonAsync<NextVmidDto>($"/api/deploy/vmid?installation={Installation}&from=330"))!.Vmid);
    }

    [Fact]
    public async Task A_deploy_whose_guest_is_set_otherwise_before_it_is_synced_is_superseded_and_never_approved_at_deploy()
    {
        var setup = await StartAsync(Lab("policy: { approval: manual }", guests: false), agent: true);
        await using var _ = setup;
        // The deploy's commit, as the wizard writes it; then, before any sync reads it, someone changes the new guest.
        var request = Guest(setup.RemoteHead, 311, approve: true);
        var preview = await PreviewAsync(setup, request);
        const string Text = "schemaVersion: 1\ntype: vm\nhost: korriban\nvmid: 311\nworkflow: test-vm@1\nhardware:\n  cores: 2\n  memory: 2048\n"
            + "network:\n  bridge: vmbr0\n  mode: static\n  address: 10.1.0.40/24\n  gateway: 10.1.0.1\n";
        setup.PushElsewhere(NewFile, Text);
        var deployed = setup.RemoteHead;
        var id = Guid.CreateVersion7();
        await setup.Server.WithDbAsync(async db =>
        {
            db.Changes.Add(new ResourceChange
            {
                Id = id, Kind = ChangeKinds.CreateGuest, ResourceKind = ResourceKinds.Guest, Address = NewGuest, Author = "ops",
                BaseCommit = preview.BaseCommit, Message = preview.Subject, Commit = deployed, Status = ChangeStatuses.Committed, Vmid = 311,
                Edited = """{"host":"korriban","vmid":311,"workflow":"test-vm@1","hardware":{"cores":2,"memory":2048},"network":{"bridge":"vmbr0","mode":"static","address":"10.1.0.40/24","gateway":"10.1.0.1"}}""",
                PreApproval = PreApprovals.Requested, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        setup.PushElsewhere(NewFile, Text.Replace("memory: 2048", "memory: 4096"));
        await setup.Server.SyncAsync();

        var change = (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{id}"))!;
        Assert.Equal(ChangeStatuses.Superseded, change.Status);
        Assert.Contains("it sets hardware otherwise", change.Detail);

        // The guest's first plan is the later commit's: it waits for a person, whatever the deploy asked.
        await setup.Agent!.ReportAsync(await setup.Agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await setup.Server.TickAsync();
        var run = await setup.Server.LatestRunAsync(NewGuest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Null(run.Steps.Single(s => s.Name == StepNames.Approval).Decision);
    }

    [Fact]
    public async Task A_generator_is_used_only_where_the_workflow_asks_for_it()
    {
        await using var setup = await StartAsync();
        var head = setup.RemoteHead;
        var wrong = await RefusedAsync(setup, Guest(head, 320, workflow: "agent-vm@1",
            secrets: new Dictionary<string, SecretChoice> { ["enroll_token"] = new(Generate: SecretGenerators.RandomPassword) }), "/api/deploy/preview");
        Assert.Contains("asks for enroll_token to be generated as agent-enrollment-token, not random-password", wrong.Message);
        var missing = await RefusedAsync(setup, Guest(head, 320, workflow: "agent-vm@1"), "/api/deploy/preview");
        Assert.Contains("needs the secret enroll_token", missing.Message);
        var existing = await PreviewAsync(setup, Guest(head, 320, workflow: "agent-vm@1",
            secrets: new Dictionary<string, SecretChoice> { ["enroll_token"] = new(Store: "shared/enroll") }));
        Assert.Empty(existing.Generated!);
        Assert.Contains("+  enroll_token: { store: shared/enroll }", existing.Diff);
    }

    [Fact]
    public async Task A_new_service_is_one_commit_of_its_service_yaml_under_its_guest()
    {
        await using var setup = await StartAsync();
        var request = new DeployRequest(ChangeKinds.CreateService, setup.RemoteHead, Existing, "edge", "rules@2", Inputs: new JsonObject { ["port"] = 8080 });
        var preview = await PreviewAsync(setup, request);
        Assert.True(preview.Valid, string.Join("; ", preview.Issues.Select(i => i.Message)));
        var change = await DeployAsync(setup, request);
        Assert.Equal(ChangeKinds.CreateService, change.Kind);
        Assert.Equal("home/korriban/krb-app-01/edge", change.Address);
        Assert.Equal("""
            # edge: deployed from rules@2.0 with Octopushy's workflow wizard.
            # Firewall rules.
            schemaVersion: 1
            workflow: rules@2                 # the newest 2.x; rules@2.0 pins this one
            inputs:
              port: 8080

            """.ReplaceLineEndings("\n"), setup.RemoteFile("labs/home/proxmox/korriban/guests/krb-app-01/services/edge/service.yaml"));
        Assert.Equal((await setup.Server.ResourceAsync("home/korriban/krb-app-01/edge")).Id, change.ResourceId);

        var vm = await RefusedAsync(setup, request with { Name = "edge-2", Workflow = "test-vm@1" }, "/api/deploy/preview");
        Assert.Contains("manages vm, not a service", vm.Message);
        var hardware = await RefusedAsync(setup, request with { Name = "edge-2", Hardware = new JsonObject { ["cores"] = 1 } }, "/api/deploy/preview");
        Assert.Contains("A service has inputs and secrets only", hardware.Message);
    }

    /// <summary>Deploys a new guest to a lab with manual approval, asking to approve in the same click, and reports its plan.</summary>
    private async Task<(Setup Setup, ChangeDto Change, Run Run)> DeployAndPlanAsync(JsonObject plan, bool approve = true)
    {
        var setup = await StartAsync(Lab("policy: { approval: manual }", guests: false), agent: true);
        var change = await DeployAsync(setup, Guest(setup.RemoteHead, 311, approve: approve));
        Assert.Equal(approve ? PreApprovals.Requested : null, change.PreApproval);
        await setup.Agent!.ReportAsync(await setup.Agent.NextAsync("preview"), Outcome.Succeeded, plan, artifacts: FakeAgent.PlanArtifact());
        await setup.Server.TickAsync();
        return (setup, change, await setup.Server.LatestRunAsync(NewGuest, Operations.Provision));
    }

    [Fact]
    public async Task Approving_in_the_same_click_approves_a_first_plan_that_only_creates()
    {
        var (setup, change, run) = await DeployAndPlanAsync(FakeAgent.Plan(create: 1));
        await using var _ = setup;
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal("approved", approval.Decision);
        Assert.Equal("ops", approval.DecidedBy);
        Assert.True(approval.ApprovalRequired);
        Assert.StartsWith("Approved at deploy (change ", approval.DecisionNote);

        var apply = await setup.Agent!.NextAsync("apply");
        await setup.Agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await setup.Agent.ReportAsync(await setup.Agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        change = (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{change.Id}"))!;
        Assert.Equal(ChangeStatuses.Applied, change.Status);
        Assert.Equal(PreApprovals.Approved, change.PreApproval);
        Assert.Equal($"Run #{run.Number}'s plan was approved at deploy by ops: it only creates.", change.PreApprovalDetail);

        // It never covers a later plan: a change of the guest's memory waits for approval as the policy says.
        var form = (await setup.Client.GetFromJsonAsync<ChangeFormDto>($"/api/changes/form/{change.ResourceId}"))!;
        var edit = await setup.Client.PostAsJsonAsync("/api/changes", new ChangeRequest(change.ResourceId!.Value, form.BaseCommit!,
            Hardware: new JsonObject { ["cores"] = 2, ["memory"] = 4096 }));
        edit.EnsureSuccessStatusCode();
        await setup.Agent.ReportAsync(await setup.Agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await setup.Server.TickAsync();
        var later = await setup.Server.LatestRunAsync(NewGuest, Operations.Provision);
        Assert.NotEqual(run.Id, later.Id);
        Assert.Equal(RunStatuses.AwaitingApproval, later.Status);
        Assert.Null(later.Steps.Single(s => s.Name == StepNames.Approval).Decision);
    }

    [Theory]
    [InlineData(0, 1, 0, "1 to update")]
    [InlineData(0, 0, 1, "1 to delete")]
    [InlineData(1, 0, 0, "replace")]
    public async Task A_first_plan_that_does_more_than_create_waits_for_approval_and_says_why(int replace, int update, int delete, string why)
    {
        var plan = FakeAgent.Plan(create: replace == 0 ? 1 : 0, update: update, delete: delete, replace: replace);
        var (setup, change, run) = await DeployAndPlanAsync(plan);
        await using var _ = setup;
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Null(approval.Decision);
        Assert.Contains("Not approved at deploy: Plan does more than create; approve it yourself.", approval.Detail);
        Assert.Contains(why, approval.Detail);
        change = (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{change.Id}"))!;
        Assert.Equal(ChangeStatuses.WaitingForApproval, change.Status);
        Assert.Equal(PreApprovals.Declined, change.PreApproval);
        Assert.StartsWith("Plan does more than create; approve it yourself.", change.PreApprovalDetail);
    }

    [Fact]
    public async Task Without_approval_in_the_same_click_a_create_only_plan_still_waits()
    {
        var (setup, change, run) = await DeployAndPlanAsync(FakeAgent.Plan(create: 1), approve: false);
        await using var _ = setup;
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.DoesNotContain("deploy", run.Steps.Single(s => s.Name == StepNames.Approval).Detail);
        Assert.Null((await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{change.Id}"))!.PreApproval);
    }

    [Fact]
    public async Task The_plan_of_a_revision_after_the_deploys_is_not_approved_at_deploy()
    {
        var setup = await StartAsync(Lab("policy: { approval: manual }", guests: false), agent: true);
        await using var _ = setup;
        var change = await DeployAsync(setup, Guest(setup.RemoteHead, 311, approve: true));
        // The guest is changed again before its first plan is decided: that plan is of the newer revision.
        var form = (await setup.Client.GetFromJsonAsync<ChangeFormDto>($"/api/changes/form/{change.ResourceId}"))!;
        (await setup.Client.PostAsJsonAsync("/api/changes", new ChangeRequest(change.ResourceId!.Value, form.BaseCommit!,
            Hardware: new JsonObject { ["cores"] = 2, ["memory"] = 4096 }))).EnsureSuccessStatusCode();
        var assignment = await setup.Agent!.NextAsync("preview");
        await setup.Agent.ReportAsync(assignment, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await setup.Server.TickAsync();
        var runs = await setup.Server.QueryAsync(db => db.Runs.Include(r => r.Steps).Where(r => r.Operation == Operations.Provision).ToListAsync());
        Assert.All(runs.SelectMany(r => r.Steps).Where(s => s.Name == StepNames.Approval), s => Assert.NotEqual("approved", s.Decision));
        change = (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{change.Id}"))!;
        Assert.NotEqual(PreApprovals.Approved, change.PreApproval);
    }
}
