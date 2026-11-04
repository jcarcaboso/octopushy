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
                ts_key: { requiredWhen: { pointer: /inputs/features, value: tailscale } }
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

        /// <summary>Records a successful observation of korriban at <paramref name="at"/>, as an agent would report one.</summary>
        public Task ObserveAsync(DateTimeOffset? at = null, bool online = true, params (int Vmid, string Name)[] vms) =>
            ObserveRawAsync(new JsonObject
            {
                ["cluster"] = null,
                ["hosts"] = new JsonArray(new JsonObject { ["name"] = "korriban", ["online"] = online }),
                ["guests"] = new JsonArray(vms.Select(vm => (JsonNode)new JsonObject { ["vmid"] = vm.Vmid, ["name"] = vm.Name, ["host"] = "korriban" }).ToArray()),
                ["vmVisibility"] = new JsonObject { ["complete"] = true },
            }, at);

        /// <summary>
        /// Records an observation of korriban with <paramref name="data"/>, through korriban's connection unless the data names
        /// one, as the server records it.
        /// </summary>
        public Task ObserveRawAsync(JsonObject data, DateTimeOffset? at = null) =>
            Server.WithDbAsync(async db =>
            {
                if (!data.ContainsKey("connection"))
                    data["connection"] = ObservedConnection.Of("https://10.1.0.10:8006/", false, null);
                var installation = await db.Resources.FirstAsync(r => r.Address == Installation);
                db.Observations.Add(new Observation
                {
                    Id = Guid.CreateVersion7(), ResourceId = installation.Id, ObservedAt = at ?? DateTimeOffset.UtcNow, Data = data.ToJsonString(),
                });
                await db.SaveChangesAsync();
            });

        public async Task<DeployTargetsDto> TargetsAsync() => (await Client.GetFromJsonAsync<DeployTargetsDto>("/api/deploy/targets"))!;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
            Remote.Dispose();
        }
    }

    private async Task<Setup> StartAsync(TestRepository? lab = null, bool agent = false, bool observe = true)
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
        var setup = new Setup { Server = server, Client = client, Remote = remote, Agent = fake };
        // Free VMIDs are only known from a recent, successful and complete observation.
        if (observe)
            await setup.ObserveAsync();
        return setup;
    }

    private static DeployRequest Guest(string baseCommit, int vmid, string name = "new-01", string workflow = "test-vm@1", string parent = Installation,
        IReadOnlyDictionary<string, SecretChoice>? secrets = null, JsonObject? inputs = null, bool approve = false, int memory = 2048) =>
        new(ChangeKinds.CreateGuest, baseCommit, parent, name, workflow, Host: parent == Installation ? "korriban" : "watch", Vmid: vmid,
            Hardware: new JsonObject { ["cores"] = 2, ["memory"] = memory },
            Network: new JsonObject { ["bridge"] = "vmbr0", ["mode"] = "static", ["address"] = "10.1.0.40/24", ["gateway"] = "10.1.0.1" },
            Inputs: inputs, Secrets: secrets, ApproveAtDeploy: approve);

    /// <summary>The request as the wizard sends it after its review: bound to the release and files the review showed.</summary>
    private static async Task<DeployRequest> ReviewedAsync(Setup setup, DeployRequest request)
    {
        if (request.ReviewDigest is not null)
            return request;
        var response = await setup.Client.PostAsJsonAsync("/api/deploy/preview", request);
        if (!response.IsSuccessStatusCode)
            return request;
        var preview = (await response.Content.ReadFromJsonAsync<ChangePreviewDto>())!;
        return request with { ReviewedRelease = preview.ReviewedRelease, ReviewDigest = preview.ReviewDigest };
    }

    private static async Task<ChangeDto> DeployAsync(Setup setup, DeployRequest request)
    {
        var response = await setup.Client.PostAsJsonAsync("/api/deploy", await ReviewedAsync(setup, request));
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ChangeDto>())!;
    }

    private static async Task<ChangeRefusedDto> RefusedAsync(Setup setup, DeployRequest request, string path = "/api/deploy")
    {
        var response = await setup.Client.PostAsJsonAsync(path, path == "/api/deploy" ? await ReviewedAsync(setup, request) : request);
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
            (await setup.Client.GetFromJsonAsync<NextVmidDto>($"/api/deploy/vmid?installation={Installation}&from={from}"))!.Vmid!.Value;

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
                    ["cluster"] = null,
                    ["vmVisibility"] = new JsonObject { ["complete"] = true },
                    ["connection"] = ObservedConnection.Of("https://10.1.0.10:8006/", false, null),
                    ["hosts"] = new JsonArray(new JsonObject { ["name"] = "korriban", ["online"] = true }),
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
        Assert.Equal("online", (await setup.TargetsAsync()).Installations.Single(i => i.Address == Installation).Hosts.Single().Status);
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
        request = request with { ReviewedRelease = preview.ReviewedRelease, ReviewDigest = preview.ReviewDigest };

        // Someone declares 311 on the branch between the review and the deploy.
        setup.PushElsewhere("labs/home/proxmox/korriban/guests/other-01/guest.yaml",
            "schemaVersion: 1\ntype: vm\nhost: korriban\nvmid: 311\nworkflow: test-vm@1\nhardware: { cores: 1, memory: 512 }\nnetwork: { bridge: vmbr0, mode: dhcp }\n");
        var moved = setup.RemoteHead;
        var refused = await RefusedAsync(setup, request);
        Assert.Equal(ChangeRefusal.Reasons.VmidTaken, refused.Reason);
        Assert.Contains("written in labs/home/proxmox/korriban/guests/other-01/guest.yaml", refused.Message);
        Assert.Equal(312, refused.NextVmid);
        Assert.Equal(moved, setup.RemoteHead);

        // Nothing generated is left behind: no store value, no enrollment token.
        Assert.False(await setup.Server.QueryAsync(db => db.StoreEntries.AnyAsync(e => e.Key.StartsWith("guests/"))));
        Assert.Equal(0, await setup.Server.QueryAsync(db => db.EnrollmentTokens.CountAsync()));
        var recorded = await setup.Server.QueryAsync(db => db.Changes.SingleAsync(c => c.Id == refused.ChangeId));
        Assert.Equal(ChangeStatuses.Refused, recorded.Status);
        Assert.All(await setup.Server.QueryAsync(db => db.GeneratedValues.AsNoTracking().Where(v => v.ChangeId == recorded.Id).ToListAsync()),
            v => Assert.Equal(GeneratedValueStates.Removed, v.State));
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
        Assert.Equal([Key, "guests/home/korriban/new-01/admin_password"], change.Generated!.Select(g => g.Key));
        // What its file references is recorded before the push, for cleanup when a candidate is not in the mirror.
        var recordedKeys = (await setup.Server.QueryAsync(db => db.Changes.AsNoTracking().SingleAsync(c => c.Id == change.Id))).StoreKeys!;
        Assert.Contains(Key, recordedKeys);
        Assert.Contains("guests/home/korriban/new-01/host-key", recordedKeys);
        Assert.All(change.Generated!, g => Assert.Equal(GeneratedValueStates.Created, g.State));
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

    /// <summary>
    /// Records a deploy of <paramref name="address"/> in <paramref name="status"/> that generated <paramref name="key"/>, as the
    /// wizard leaves it: the change, the value's record of what it owns, the store entry it wrote and, if asked, its token.
    /// </summary>
    private static async Task<(Guid Change, Guid? Token)> OwnedAsync(Setup setup, string status, string key, string address = "home/korriban/lost-01",
        bool token = false, bool tokenUsed = false, int? vmid = null)
    {
        var id = Guid.CreateVersion7();
        var generation = Guid.CreateVersion7();
        var marker = $"Generated at deploy for {address} (agent-enrollment-token, change {id}).";
        Guid? tokenId = null;
        await setup.Server.WithDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            db.Changes.Add(new ResourceChange
            {
                Id = id, Kind = ChangeKinds.CreateGuest, ResourceKind = ResourceKinds.Guest, Address = address, Author = "ops",
                BaseCommit = setup.RemoteHead, Message = "deploy", Status = status, Vmid = vmid, CreatedAt = now, UpdatedAt = now,
                CandidateCommits = status == ChangeStatuses.Committing ? [] : [new string('c', 40)],
            });
            if (token)
            {
                var ca = setup.Server.Services.GetRequiredService<Octopushy.Server.Agents.AgentCertificateAuthority>();
                var (made, _) = Octopushy.Server.Agents.EnrollmentTokens.Create(db, ca, address.Split('/')[^1], ["home"], 2, 60, now);
                if (tokenUsed)
                    made.UsedAt = now;
                tokenId = made.Id;
            }

            db.GeneratedValues.Add(new GeneratedValue
            {
                Id = generation, ChangeId = id, Secret = "enroll_token", Key = key, Generator = SecretGenerators.AgentEnrollmentToken,
                State = GeneratedValueStates.Created, StoreVersion = 1, StoreDescription = marker, TokenId = tokenId, CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        });
        await using var scope = setup.Server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<Octopushy.Server.Store.VariableStore>().SetAsync(key, "a generated value", Visibilities.Secret, marker,
            CancellationToken.None, generation);
        return (id, tokenId);
    }

    private static Task<GeneratedValue> ValueOfAsync(Setup setup, Guid change) =>
        setup.Server.QueryAsync(db => db.GeneratedValues.AsNoTracking().SingleAsync(v => v.ChangeId == change));

    private static Task<bool> StoredAsync(Setup setup, string key) => setup.Server.QueryAsync(db => db.StoreEntries.AnyAsync(e => e.Key == key));

    [Fact]
    public async Task A_deploy_whose_request_ended_before_its_push_is_refused_by_reconciliation_and_its_generated_values_removed()
    {
        await using var setup = await StartAsync();
        // As a request cancelled before its push leaves it: recorded as committing, its token and value made, no candidate commit.
        const string Key = "guests/home/korriban/lost-01/enroll_token";
        var (id, token) = await OwnedAsync(setup, ChangeStatuses.Committing, Key, token: true, vmid: 330);
        Assert.Equal(331, (await setup.Client.GetFromJsonAsync<NextVmidDto>($"/api/deploy/vmid?installation={Installation}&from=330"))!.Vmid);

        await setup.Server.SyncAsync();
        Assert.Equal(ChangeStatuses.Refused, (await setup.Server.QueryAsync(db => db.Changes.AsNoTracking().SingleAsync(c => c.Id == id))).Status);
        var value = await ValueOfAsync(setup, id);
        Assert.Equal(GeneratedValueStates.Removed, value.State);
        Assert.Contains("version 1 deleted", value.Detail);
        Assert.False(await StoredAsync(setup, Key));
        Assert.False(await setup.Server.QueryAsync(db => db.EnrollmentTokens.AnyAsync(t => t.Id == token)));
        // It no longer holds its VMID.
        Assert.Equal(330, (await setup.Client.GetFromJsonAsync<NextVmidDto>($"/api/deploy/vmid?installation={Installation}&from=330"))!.Vmid);
    }

    [Fact]
    public async Task Cleanup_of_an_abandoned_deploy_never_removes_what_a_later_deploy_or_a_person_wrote_under_its_key()
    {
        await using var setup = await StartAsync();
        const string Key = "guests/home/korriban/lost-01/enroll_token";
        // A was superseded; its value went, and B, a later deploy of the same name still being pushed, wrote the same key.
        var (a, _) = await OwnedAsync(setup, ChangeStatuses.Superseded, Key);
        await setup.Server.WithDbAsync(async db =>
        {
            await db.StoreVersions.Where(v => v.Key == Key).ExecuteDeleteAsync();
            await db.StoreEntries.Where(e => e.Key == Key).ExecuteDeleteAsync();
        });
        var (b, _) = await OwnedAsync(setup, ChangeStatuses.Committing, Key);
        // B's request is still running: its change is not settled by this sync.
        await setup.Server.WithDbAsync(async db =>
        {
            var record = await db.Changes.SingleAsync(c => c.Id == b);
            record.CandidateCommits = [new string('d', 40)];
            record.Status = ChangeStatuses.Uncertain;
            await db.SaveChangesAsync();
        });

        await setup.Server.SyncAsync();
        // A's record matches the key's version, not its writer: B's value stays, and so does B's record.
        Assert.Equal(GeneratedValueStates.Kept, (await ValueOfAsync(setup, a)).State);
        Assert.Contains("written again since", (await ValueOfAsync(setup, a)).Detail);
        Assert.True(await StoredAsync(setup, Key));
        Assert.Equal(GeneratedValueStates.Created, (await ValueOfAsync(setup, b)).State);
        var entry = await setup.Server.QueryAsync(db => db.StoreEntries.AsNoTracking().SingleAsync(e => e.Key == Key));
        Assert.Contains(b.ToString(), entry.Description);

        // A person who deletes a key a finished deploy generated and sets it again by hand keeps it too, even at version 1
        // and with the very same description copied over: the version was not made by that deploy.
        const string Manual = "guests/home/korriban/manual-01/enroll_token";
        var (c, _) = await OwnedAsync(setup, ChangeStatuses.Superseded, Manual, address: "home/korriban/manual-01");
        var copied = (await setup.Server.QueryAsync(db => db.StoreEntries.AsNoTracking().SingleAsync(e => e.Key == Manual))).Description;
        await setup.Server.WithDbAsync(async db =>
        {
            await db.StoreVersions.Where(v => v.Key == Manual).ExecuteDeleteAsync();
            await db.StoreEntries.Where(e => e.Key == Manual).ExecuteDeleteAsync();
        });
        await using (var scope = setup.Server.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<Octopushy.Server.Store.VariableStore>().SetAsync(Manual, "set by hand", Visibilities.Secret, copied,
                CancellationToken.None);
        var replaced = await setup.Server.QueryAsync(db => db.StoreEntries.AsNoTracking().SingleAsync(e => e.Key == Manual));
        Assert.Equal((1, copied), (replaced.LatestVersion, replaced.Description));
        await setup.Server.SyncAsync();
        Assert.Equal(GeneratedValueStates.Kept, (await ValueOfAsync(setup, c)).State);
        Assert.True(await StoredAsync(setup, Manual));
    }

    [Fact]
    public async Task A_removal_that_fails_stays_pending_shows_on_the_change_and_is_retried_at_the_next_sync()
    {
        await using var setup = await StartAsync();
        const string Key = "guests/home/korriban/lost-01/enroll_token";
        var (id, _) = await OwnedAsync(setup, ChangeStatuses.Superseded, Key);

        // Someone holds the entry's row: the removal waits for its lock, gives up, and is left for the next sync.
        await using (var scope = setup.Server.Services.CreateAsyncScope())
        {
            var other = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
            await using var held = await other.Database.BeginTransactionAsync();
            await other.Database.ExecuteSqlRawAsync("SELECT 1 FROM store_entries WHERE key = {0} FOR UPDATE", Key);
            await setup.Server.SyncAsync();
            await held.RollbackAsync();
        }

        var pending = await ValueOfAsync(setup, id);
        Assert.True(pending.CleanupPending);
        Assert.Equal(1, pending.CleanupAttempts);
        Assert.Contains("lock", pending.CleanupError, StringComparison.OrdinalIgnoreCase);
        Assert.True(await StoredAsync(setup, Key));
        var change = (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{id}"))!;
        Assert.Equal(1, change.CleanupPending);
        Assert.True(change.Generated!.Single().CleanupPending);

        await setup.Server.SyncAsync();
        var removed = await ValueOfAsync(setup, id);
        Assert.Equal(GeneratedValueStates.Removed, removed.State);
        Assert.False(removed.CleanupPending);
        Assert.False(await StoredAsync(setup, Key));
        Assert.Equal(0, (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{id}"))!.CleanupPending);
    }

    [Fact]
    public async Task A_dismissed_deploy_whose_commit_turns_up_keeps_what_the_definition_uses()
    {
        await using var setup = await StartAsync();
        const string Key = "guests/home/korriban/landed-01/enroll_token";
        var (id, token) = await OwnedAsync(setup, ChangeStatuses.Dismissed, Key, address: "home/korriban/landed-01", token: true);
        // Its commit was on the branch after all: the head defines the guest, and it uses the generated key.
        setup.PushElsewhere("labs/home/proxmox/korriban/guests/landed-01/guest.yaml",
            $"schemaVersion: 1\ntype: vm\nhost: korriban\nvmid: 350\nworkflow: agent-vm@1\nhardware: {{ cores: 1, memory: 512 }}\n"
            + $"network: {{ bridge: vmbr0, mode: dhcp }}\nsecrets:\n  enroll_token: {{ store: {Key} }}\n");
        await setup.Server.SyncAsync();
        await setup.Server.SyncAsync();
        var value = await ValueOfAsync(setup, id);
        Assert.Equal(GeneratedValueStates.Created, value.State);
        Assert.False(value.CleanupPending);
        Assert.True(await StoredAsync(setup, Key));
        Assert.True(await setup.Server.QueryAsync(db => db.EnrollmentTokens.AnyAsync(t => t.Id == token)));

        // A definition that does not even validate still counts as using it.
        setup.PushElsewhere("labs/home/proxmox/korriban/guests/landed-01/guest.yaml",
            $"schemaVersion: 1\ntype: vm\nhost: korriban\nvmid: 350\ncolour: blue\nsecrets:\n  enroll_token: {{ store: {Key} }}\n");
        await setup.Server.SyncAsync();
        Assert.False((await setup.Server.ResourceAsync("home/korriban/landed-01")).Valid);
        Assert.Equal(GeneratedValueStates.Created, (await ValueOfAsync(setup, id)).State);

        // Nothing references it any more, and it stays all the same: a dismissal proves nothing, so the change is not final and
        // one of its commits may still arrive.
        setup.PushElsewhere("labs/home/proxmox/korriban/guests/landed-01/guest.yaml",
            "schemaVersion: 1\ntype: vm\nhost: korriban\nvmid: 350\nworkflow: test-vm@1\nhardware: { cores: 1, memory: 512 }\nnetwork: { bridge: vmbr0, mode: dhcp }\n");
        await setup.Server.SyncAsync();
        Assert.Equal(ChangeStatuses.Dismissed, (await setup.Server.QueryAsync(db => db.Changes.AsNoTracking().SingleAsync(c => c.Id == id))).Status);
        Assert.Equal(GeneratedValueStates.Created, (await ValueOfAsync(setup, id)).State);
        Assert.True(await StoredAsync(setup, Key));

        // Once the change is final and nothing references the key, it goes; a token that was used stays, since removing it
        // would not affect its agent.
        await setup.Server.WithDbAsync(async db =>
        {
            (await db.EnrollmentTokens.SingleAsync(t => t.Id == token)).UsedAt = DateTimeOffset.UtcNow;
            (await db.Changes.SingleAsync(c => c.Id == id)).Status = ChangeStatuses.Superseded;
            await db.SaveChangesAsync();
        });
        await setup.Server.SyncAsync();
        value = await ValueOfAsync(setup, id);
        Assert.Equal(GeneratedValueStates.Removed, value.State);
        Assert.Contains("was used, so it stays", value.Detail);
        Assert.False(await StoredAsync(setup, Key));
        Assert.True(await setup.Server.QueryAsync(db => db.EnrollmentTokens.AnyAsync(t => t.Id == token)));
    }

    [Fact]
    public void A_store_reference_counts_in_any_definition_file_valid_or_not_and_an_unreadable_one_keeps_everything()
    {
        using var lab = new TestRepository();
        lab.Write("octopushy.yaml", "schemaVersion: 1\n");
        // Not a valid guest, with the reference in JSON style: it still names the key.
        lab.Write("labs/home/proxmox/korriban/guests/odd-01/guest.yaml", """{ "colour": "blue", "secrets": { "pw": { "store": "guests/home/korriban/odd-01/password" } } }""");
        lab.Write("labs/home/proxmox/korriban/guests/deep-01/guest.yaml", "list:\n  - nested: [ { store: 'guests/home/korriban/deep-01/key' } ]\n");
        var keys = GeneratedValues.ReferencedKeys(lab.Root)!;
        Assert.Contains("guests/home/korriban/odd-01/password", keys);
        Assert.Contains("guests/home/korriban/deep-01/key", keys);

        lab.Write("labs/home/proxmox/korriban/guests/garbled-01/guest.yaml", "secrets: [\n  : {\n");
        Assert.Null(GeneratedValues.ReferencedKeys(lab.Root));
    }

    [Fact]
    public async Task Dismissing_a_deploy_removes_nothing_by_itself()
    {
        await using var setup = await StartAsync();
        const string Key = "guests/home/korriban/lost-01/enroll_token";
        var (id, _) = await OwnedAsync(setup, ChangeStatuses.Unknown, Key);
        var dismissed = await setup.Client.PostAsync($"/api/changes/{id}/dismiss", null);
        Assert.True(dismissed.IsSuccessStatusCode, await dismissed.Content.ReadAsStringAsync());
        // A push was attempted: one of its commits could still land, so the dismissal itself takes nothing away.
        Assert.Equal(GeneratedValueStates.Created, (await ValueOfAsync(setup, id)).State);
        Assert.True(await StoredAsync(setup, Key));

        // The next sync reads the branch it has just fetched, which now references the key: it stays.
        setup.PushElsewhere("labs/home/proxmox/korriban/guests/lost-01/guest.yaml",
            $"schemaVersion: 1\ntype: vm\nhost: korriban\nvmid: 330\ncolour: blue\nsecrets: {{ enroll_token: {{ store: {Key} }} }}\n");
        await setup.Server.SyncAsync();
        Assert.Equal(GeneratedValueStates.Created, (await ValueOfAsync(setup, id)).State);
        Assert.True(await StoredAsync(setup, Key));
    }

    [Fact]
    public async Task A_key_a_commit_of_an_open_change_references_is_not_removed()
    {
        await using var setup = await StartAsync();
        const string Key = "guests/home/korriban/lost-01/enroll_token";
        const string File = "labs/home/proxmox/korriban/guests/other-01/guest.yaml";
        var (id, _) = await OwnedAsync(setup, ChangeStatuses.Superseded, Key);
        // Another change, not settled, recorded a commit that references the key; the branch head does not.
        setup.PushElsewhere(File, $"schemaVersion: 1\ntype: vm\nhost: korriban\nvmid: 340\nsecrets: {{ x: {{ store: {Key} }} }}\n");
        await setup.Server.SyncAsync();
        var referencing = setup.RemoteHead;
        await setup.Server.WithDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Changes.Add(new ResourceChange
            {
                Id = Guid.CreateVersion7(), Kind = ChangeKinds.Edit, ResourceKind = ResourceKinds.Guest, Address = "home/korriban/other-01", Author = "ops",
                BaseCommit = referencing, Message = "other-01: edit", Status = ChangeStatuses.Uncertain, Files = [File], CandidateCommits = [referencing],
                CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        });
        TestWorkflows.Run(setup.Remote.Root, "update-ref", "refs/heads/main", $"{referencing}^");
        await setup.Server.SyncAsync();
        Assert.Equal(GeneratedValueStates.Created, (await ValueOfAsync(setup, id)).State);
        Assert.True(await StoredAsync(setup, Key));
    }

    [Fact]
    public async Task A_key_an_open_change_recorded_writing_is_not_removed_even_when_its_commit_is_not_here()
    {
        await using var setup = await StartAsync();
        const string Key = "guests/home/korriban/lost-01/enroll_token";
        var (id, _) = await OwnedAsync(setup, ChangeStatuses.Superseded, Key);
        // Another deploy of the same guest, unknown, writes the key in what it recorded; its candidate is not in the mirror.
        await setup.Server.WithDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Changes.Add(new ResourceChange
            {
                Id = Guid.CreateVersion7(), Kind = ChangeKinds.CreateGuest, ResourceKind = ResourceKinds.Guest, Address = "home/korriban/lost-01",
                Author = "ops", BaseCommit = setup.RemoteHead, Message = "lost-01: deploy", Status = ChangeStatuses.Unknown, Files = [NewFile],
                CandidateCommits = [new string('e', 40)], Edited = new JsonObject { ["secrets"] = new JsonObject { ["enroll_token"] = new JsonObject { ["store"] = Key } } }.ToJsonString(), CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        });
        await setup.Server.SyncAsync();
        Assert.Equal(GeneratedValueStates.Created, (await ValueOfAsync(setup, id)).State);
        Assert.True(await StoredAsync(setup, Key));
    }

    [Fact]
    public async Task An_open_edit_whose_candidate_is_not_here_keeps_every_key_its_written_file_references()
    {
        await using var setup = await StartAsync();
        const string Key = "guests/home/korriban/lost-01/enroll_token";
        var (owned, _) = await OwnedAsync(setup, ChangeStatuses.Superseded, Key);
        // An ordinary edit of another guest: it changes only hardware, and the file it writes keeps a reference to the key in a
        // section it does not change. Its candidate commit is not in the mirror.
        const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        var text = setup.RemoteFile(GuestFile) + $"secrets: {{ enroll_token: {{ store: {Key} }} }}\n";
        var composed = ChangeComposer.Compose(new ChangeRequest(Guid.NewGuid(), setup.RemoteHead, Hardware: new JsonObject { ["cores"] = 4, ["memory"] = 2048 }),
            Guid.NewGuid(), ResourceKinds.Guest, Existing, GuestFile, "labs/home/proxmox/korriban/guests/krb-app-01", text, []);
        Assert.DoesNotContain(Key, composed.Edited!.ToJsonString());
        Assert.Contains(Key, GeneratedValues.StoreKeysOf(composed));
        async Task OpenEditAsync(List<string>? storeKeys) => await setup.Server.WithDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Changes.Add(new ResourceChange
            {
                Id = Guid.CreateVersion7(), Kind = ChangeKinds.Edit, ResourceKind = ResourceKinds.Guest, Address = Existing, Author = "ops",
                BaseCommit = setup.RemoteHead, Message = "krb-app-01: cores 2 → 4", Status = ChangeStatuses.Unknown, Files = [GuestFile],
                CandidateCommits = [new string('e', 40)], Edited = composed.Edited!.ToJsonString(), StoreKeys = storeKeys, CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        });

        // Recorded before its push, the keys its file references stand for the commit that is not here.
        await OpenEditAsync(GeneratedValues.StoreKeysOf(composed));
        await setup.Server.SyncAsync();
        Assert.Equal(GeneratedValueStates.Created, (await ValueOfAsync(setup, owned)).State);
        Assert.True(await StoredAsync(setup, Key));

        // A change recorded before the keys were kept tells nothing about a missing commit: nothing is removed.
        await setup.Server.WithDbAsync(async db => await db.Changes.Where(c => c.Kind == ChangeKinds.Edit).ExecuteDeleteAsync());
        await OpenEditAsync(null);
        await setup.Server.SyncAsync();
        Assert.Equal(GeneratedValueStates.Created, (await ValueOfAsync(setup, owned)).State);

        // Settled, the key goes.
        await setup.Server.WithDbAsync(async db => await db.Changes.Where(c => c.Kind == ChangeKinds.Edit)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, ChangeStatuses.Superseded)));
        await setup.Server.SyncAsync();
        Assert.Equal(GeneratedValueStates.Removed, (await ValueOfAsync(setup, owned)).State);
    }

    [Theory]
    [InlineData(ChangeStatuses.Superseded, false)]
    [InlineData(ChangeStatuses.Dismissed, true)]
    [InlineData(ChangeStatuses.Refused, false)]
    [InlineData(ChangeStatuses.Failed, false)]
    [InlineData(ChangeStatuses.Applied, false)]
    [InlineData(ChangeStatuses.Unknown, true)]
    [InlineData(ChangeStatuses.Planning, true)]
    public async Task A_final_deploy_releases_its_vmid_hold_and_a_dismissed_one_keeps_it(string status, bool held)
    {
        await using var setup = await StartAsync();
        await setup.Server.WithDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Changes.Add(new ResourceChange
            {
                Id = Guid.CreateVersion7(), Kind = ChangeKinds.CreateGuest, ResourceKind = ResourceKinds.Guest, Address = "home/korriban/held-01",
                Author = "ops", BaseCommit = setup.RemoteHead, Message = "deploy", Status = status, Vmid = 360, CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        });
        var next = (await setup.Client.GetFromJsonAsync<NextVmidDto>($"/api/deploy/vmid?installation={Installation}&from=360"))!.Vmid;
        Assert.Equal(held ? 361 : 360, next);
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
    public async Task Without_a_recent_complete_observation_no_vmid_is_offered_or_committed()
    {
        await using var setup = await StartAsync(observe: false);
        async Task<NextVmidDto> NextAsync() => (await setup.Client.GetFromJsonAsync<NextVmidDto>($"/api/deploy/vmid?installation={Installation}"))!;
        async Task ExpectAsync(string problem)
        {
            var next = await NextAsync();
            Assert.Null(next.Vmid);
            Assert.Contains(problem, next.Problem);
            Assert.Contains(problem, (await setup.TargetsAsync()).Installations.Single(i => i.Address == Installation).VmidProblem);
            var refused = await RefusedAsync(setup, Guest(setup.RemoteHead, 400));
            Assert.Equal(ChangeRefusal.Reasons.VmidsUnknown, refused.Reason);
            Assert.Contains(problem, refused.Message);
        }

        // Never observed.
        await ExpectAsync("home/korriban has never been observed successfully");
        // Observed, but longer ago than three observation intervals.
        await setup.ObserveAsync(DateTimeOffset.UtcNow.AddMinutes(-16));
        await ExpectAsync("has not been observed successfully since");
        await ExpectAsync("more than 15 minutes ago");
        // Recent, but a host was offline, so its VMs may be missing.
        await setup.ObserveAsync(DateTimeOffset.UtcNow.AddMinutes(-1), online: false);
        await ExpectAsync("did not see every host (korriban offline)");
        // Complete, but a later observation failed.
        await setup.ObserveAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(100, (await NextAsync()).Vmid);
        await setup.Server.WithDbAsync(async db =>
        {
            var installation = await db.Resources.FirstAsync(r => r.Address == Installation);
            installation.ObserveError = "The Proxmox API did not answer.";
            installation.ObserveErrorAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });
        await ExpectAsync("failed (The Proxmox API did not answer.)");
        Assert.Equal(setup.RemoteHead, (await setup.TargetsAsync()).BaseCommit);
    }

    [Theory]
    [InlineData("""{ "cluster": null, "hosts": [{ "name": "korriban", "online": true }], "guests": [] }""", "it does not say whether its token sees every VM")]
    // What an agent before this release reported: VM.Audit listed on /vms, which proves nothing about the VMs below it.
    [InlineData("""{ "cluster": null, "hosts": [{ "name": "korriban", "online": true }], "guests": [], "vmAudit": true }""", "it does not say whether its token sees every VM")]
    [InlineData("""{ "cluster": null, "hosts": [{ "name": "korriban", "online": true }], "guests": [], "vmVisibility": { "complete": false, "reason": "the token's VM.Audit on /vms does not propagate to the VMs below it" } }""",
        "does not propagate to the VMs below it")]
    [InlineData("""{ "cluster": null, "hosts": [{ "name": "korriban", "online": true }], "guests": [], "vmVisibility": { "complete": false, "reason": "ACLs on /vms/150 may narrow what the token sees below /vms" } }""",
        "ACLs on /vms/150 may narrow")]
    [InlineData("""{ "cluster": null, "hosts": [{ "name": "korriban" }], "guests": [], "vmVisibility": { "complete": true } }""", "korriban without an online state")]
    [InlineData("""{ "cluster": null, "hosts": [], "guests": [], "vmVisibility": { "complete": true } }""", "lists no host")]
    [InlineData("""{ "hosts": [{ "name": "korriban", "online": true }], "guests": [], "vmVisibility": { "complete": true } }""", "does not say whether it is a cluster")]
    [InlineData("""{ "cluster": { "name": "c" }, "hosts": [{ "name": "korriban", "online": true }], "guests": [], "vmVisibility": { "complete": true } }""",
        "does not say whether its cluster is quorate")]
    [InlineData("""{ "cluster": { "name": "c", "quorate": false }, "hosts": [{ "name": "korriban", "online": true }], "guests": [], "vmVisibility": { "complete": true } }""",
        "cluster was not quorate")]
    public async Task An_observation_that_cannot_show_it_saw_every_vm_offers_no_vmid(string data, string problem)
    {
        await using var setup = await StartAsync(observe: false);
        // The observation lists no VM, as Proxmox lists none a token may not audit: VMID 100 may well be taken.
        await setup.ObserveRawAsync(JsonNode.Parse(data)!.AsObject());
        var next = (await setup.Client.GetFromJsonAsync<NextVmidDto>($"/api/deploy/vmid?installation={Installation}"))!;
        Assert.Null(next.Vmid);
        Assert.Contains(problem, next.Problem);
        var refused = await RefusedAsync(setup, Guest(setup.RemoteHead, 100));
        Assert.Equal(ChangeRefusal.Reasons.VmidsUnknown, refused.Reason);
        Assert.Contains(problem, refused.Message);
    }

    [Fact]
    public async Task An_observation_through_another_endpoint_than_the_installation_has_at_landing_is_no_evidence()
    {
        await using var setup = await StartAsync();
        var request = Guest(setup.RemoteHead, 311);
        var preview = await PreviewAsync(setup, request);
        Assert.Null(preview.Refused);
        // The installation moves to another endpoint with the same host names, after the review and before any sync.
        const string Path = "labs/home/proxmox/korriban/installation.yaml";
        setup.PushElsewhere(Path, setup.RemoteFile(Path).Replace("10.1.0.10", "10.99.0.10"));
        var refused = await RefusedAsync(setup, request with { ReviewedRelease = preview.ReviewedRelease, ReviewDigest = preview.ReviewDigest });
        Assert.Equal(ChangeRefusal.Reasons.VmidsUnknown, refused.Reason);
        Assert.Contains("went through https://10.1.0.10:8006, and home/korriban now connects to https://10.99.0.10:8006/", refused.Message);
        Assert.DoesNotContain(NewFile, TestWorkflows.Run(setup.Remote.Root, "ls-tree", "-r", "--name-only", "main"));

        // One that does not record its connection, as before this release, is no evidence either.
        await setup.ObserveRawAsync(new JsonObject
        {
            ["connection"] = null, ["cluster"] = null, ["vmVisibility"] = new JsonObject { ["complete"] = true },
            ["hosts"] = new JsonArray(new JsonObject { ["name"] = "korriban", ["online"] = true }), ["guests"] = new JsonArray(),
        });
        Assert.Contains("does not record the connection it went through",
            (await setup.Client.GetFromJsonAsync<NextVmidDto>($"/api/deploy/vmid?installation={Installation}"))!.Problem);
    }

    [Fact]
    public async Task An_agents_observation_is_recorded_with_the_connection_it_went_through()
    {
        await using var setup = await StartAsync(agent: true, observe: false);
        var observe = await setup.Agent!.NextAsync("observe");
        await setup.Agent.ReportAsync(observe, Outcome.Succeeded, new JsonObject
        {
            ["cluster"] = null, ["vmVisibility"] = new JsonObject { ["complete"] = true },
            ["hosts"] = new JsonArray(new JsonObject { ["name"] = "korriban", ["online"] = true }), ["guests"] = new JsonArray(),
        });
        var data = await setup.Server.QueryAsync(async db => JsonNode.Parse((await db.Observations.AsNoTracking().SingleAsync()).Data)!);
        Assert.Equal("https://10.1.0.10:8006", data["connection"]!["endpoint"]!.GetValue<string>());
        Assert.False(data["connection"]!["verifyTls"]!.GetValue<bool>());
        Assert.Equal(100, (await setup.Client.GetFromJsonAsync<NextVmidDto>($"/api/deploy/vmid?installation={Installation}"))!.Vmid);
    }

    [Fact]
    public async Task A_module_a_workflow_stage_runs_changed_after_the_review_is_another_release()
    {
        await using var setup = await StartAsync();
        var request = Guest(setup.RemoteHead, 311);
        var preview = await PreviewAsync(setup, request);
        // The workflow's folder is untouched; the OpenTofu root its machine stage runs, under modules/, is not.
        setup.PushElsewhere("modules/opentofu/test-vm/main.tf",
            "variable \"guest\" { type = any }\nresource \"terraform_data\" \"different\" { input = \"changed-after-review\" }\n");
        var refused = await RefusedAsync(setup, request with { ReviewedRelease = preview.ReviewedRelease, ReviewDigest = preview.ReviewDigest });
        Assert.Equal(ChangeRefusal.Reasons.Stale, refused.Reason);
        Assert.Contains("The workflow release changed since the review", refused.Message);
        Assert.DoesNotContain(NewFile, TestWorkflows.Run(setup.Remote.Root, "ls-tree", "-r", "--name-only", "main"));
    }

    [Fact]
    public async Task An_observation_dated_ahead_of_the_servers_clock_is_not_trusted()
    {
        await using var setup = await StartAsync(observe: false);
        // A little drift is tolerated.
        await setup.ObserveAsync(DateTimeOffset.UtcNow.AddSeconds(30));
        Assert.Equal(100, (await setup.Client.GetFromJsonAsync<NextVmidDto>($"/api/deploy/vmid?installation={Installation}"))!.Vmid);
        // One dated half an hour ahead would pass any age check with a clock set back: it is refused.
        await setup.ObserveAsync(DateTimeOffset.UtcNow.AddMinutes(30));
        var next = (await setup.Client.GetFromJsonAsync<NextVmidDto>($"/api/deploy/vmid?installation={Installation}"))!;
        Assert.Null(next.Vmid);
        Assert.Contains("after the server's time now", next.Problem);
    }

    [Fact]
    public async Task A_host_added_on_the_branch_after_the_review_is_checked_against_the_observation_before_the_push()
    {
        await using var setup = await StartAsync();
        var request = Guest(setup.RemoteHead, 311);
        var preview = await PreviewAsync(setup, request);
        Assert.Null(preview.Refused);
        // Before the push, and before any sync: the installation gains a host the last observation never saw.
        var installation = setup.RemoteFile("labs/home/proxmox/korriban/installation.yaml");
        setup.PushElsewhere("labs/home/proxmox/korriban/installation.yaml", installation.Replace("hosts:\n", "hosts:\n  dagobah: {}\n"));
        var refused = await RefusedAsync(setup, request with { ReviewedRelease = preview.ReviewedRelease, ReviewDigest = preview.ReviewDigest });
        Assert.Equal(ChangeRefusal.Reasons.VmidsUnknown, refused.Reason);
        Assert.Contains("dagobah not listed", refused.Message);
        Assert.DoesNotContain(NewFile, TestWorkflows.Run(setup.Remote.Root, "ls-tree", "-r", "--name-only", "main"));
    }

    [Fact]
    public async Task A_workflow_changed_on_the_branch_without_a_sync_is_not_what_lands()
    {
        await using var setup = await StartAsync();
        var request = Guest(setup.RemoteHead, 311);
        var preview = await PreviewAsync(setup, request);
        // A compatible 1.1 of the local workflow, pushed after the review and never synced: test-vm@1 would land as 1.1.
        setup.PushElsewhere("workflows/test-vm/workflow.yaml",
            setup.RemoteFile("workflows/test-vm/workflow.yaml").Replace("version: \"1.0\"", "version: \"1.1\""));
        var refused = await RefusedAsync(setup, request with { ReviewedRelease = preview.ReviewedRelease, ReviewDigest = preview.ReviewDigest });
        Assert.Equal(ChangeRefusal.Reasons.Stale, refused.Reason);
        Assert.Contains("at the commit this deploy would land it is test-vm@1.1 from the lab repository", refused.Message);
        Assert.DoesNotContain(NewFile, TestWorkflows.Run(setup.Remote.Root, "ls-tree", "-r", "--name-only", "main"));

        // Its files changed without a new version: that is another release too.
        setup.PushElsewhere("workflows/test-vm/workflow.yaml",
            setup.RemoteFile("workflows/test-vm/workflow.yaml").Replace("version: \"1.1\"", "version: \"1.0\"").Replace("A test VM.", "A test VM, changed."));
        refused = await RefusedAsync(setup, request with { ReviewedRelease = preview.ReviewedRelease, ReviewDigest = preview.ReviewDigest });
        Assert.Contains("The workflow release changed since the review", refused.Message);
    }

    [Fact]
    public async Task A_vm_that_appeared_after_the_review_refuses_the_deploy()
    {
        await using var setup = await StartAsync();
        var request = Guest(setup.RemoteHead, 311);
        var preview = await PreviewAsync(setup, request);
        Assert.Null(preview.Refused);
        // The next observation shows a VM at 311 that someone made by hand since the review.
        await setup.ObserveAsync(null, true, (311, "made-by-hand"));
        var refused = await RefusedAsync(setup, request with { ReviewedRelease = preview.ReviewedRelease, ReviewDigest = preview.ReviewDigest });
        Assert.Equal(ChangeRefusal.Reasons.VmidTaken, refused.Reason);
        Assert.Contains("observed on korriban as made-by-hand", refused.Message);
        Assert.Equal(312, refused.NextVmid);
    }

    [Fact]
    public async Task A_guest_that_fails_validation_still_holds_its_vmid_and_one_that_is_not_yaml_stops_allocation()
    {
        await using var setup = await StartAsync();
        // Invalid: a field the guest schema does not know. Its VMID is still taken.
        setup.PushElsewhere("labs/home/proxmox/korriban/guests/broken-01/guest.yaml",
            "schemaVersion: 1\ntype: vm\nhost: korriban\nvmid: 340\nworkflow: test-vm@1\ncolour: blue\n");
        await setup.Server.SyncAsync();
        Assert.False((await setup.Server.ResourceAsync("home/korriban/broken-01")).Valid);
        Assert.Equal(341, (await setup.Client.GetFromJsonAsync<NextVmidDto>($"/api/deploy/vmid?installation={Installation}&from=340"))!.Vmid);

        // Not YAML at all: what it holds is unknown, so nothing is allocated on the installation.
        setup.PushElsewhere("labs/home/proxmox/korriban/guests/garbled-01/guest.yaml", "vmid: [342\n  : {\n");
        await setup.Server.SyncAsync();
        var next = (await setup.Client.GetFromJsonAsync<NextVmidDto>($"/api/deploy/vmid?installation={Installation}"))!;
        Assert.Null(next.Vmid);
        Assert.Contains("labs/home/proxmox/korriban/guests/garbled-01/guest.yaml cannot be read", next.Problem);
        var refused = await RefusedAsync(setup, Guest(setup.RemoteHead, 400));
        Assert.Equal(ChangeRefusal.Reasons.VmidsUnknown, refused.Reason);
    }

    [Fact]
    public async Task A_deploy_commits_only_what_was_reviewed()
    {
        await using var setup = await StartAsync();
        var request = Guest(setup.RemoteHead, 311);
        var preview = await PreviewAsync(setup, request);
        Assert.StartsWith("test-vm@1.0 from the lab repository, files ", preview.ReviewedRelease);
        Assert.Matches("^[0-9a-f]{64}$", preview.ReviewDigest!);
        var head = setup.RemoteHead;

        // Posted as is: a request that names no review.
        var unreviewed = await setup.Client.PostAsJsonAsync("/api/deploy", request);
        Assert.Equal(HttpStatusCode.Conflict, unreviewed.StatusCode);
        Assert.Contains("Review the deploy first", (await unreviewed.Content.ReadFromJsonAsync<ChangeRefusedDto>())!.Message);
        var other = await RefusedAsync(setup, request with { ReviewedRelease = preview.ReviewedRelease, ReviewDigest = new string('0', 64) });
        Assert.Contains("What the deploy would commit is not what was reviewed", other.Message);
        Assert.Equal(head, setup.RemoteHead);

        // A new release of the workflow between the review and the deploy: the reference now resolves to it.
        setup.PushElsewhere("workflows/test-vm/workflow.yaml",
            setup.RemoteFile("workflows/test-vm/workflow.yaml").Replace("version: \"1.0\"", "version: \"1.1\""));
        await setup.Server.SyncAsync();
        var changed = await RefusedAsync(setup, request with { ReviewedRelease = preview.ReviewedRelease, ReviewDigest = preview.ReviewDigest });
        Assert.Equal(ChangeRefusal.Reasons.Stale, changed.Reason);
        Assert.Contains($"The workflow release changed since the review: it was {preview.ReviewedRelease}, it is test-vm@1.1 from the lab repository", changed.Message);
    }

    [Fact]
    public async Task A_secret_a_workflow_needs_only_with_a_feature_is_required_when_that_feature_is_chosen()
    {
        await using var setup = await StartAsync();
        var token = new SecretChoice(Generate: SecretGenerators.AgentEnrollmentToken);
        var request = Guest(setup.RemoteHead, 320, workflow: "agent-vm@1", inputs: new JsonObject { ["features"] = new JsonArray("tailscale") },
            secrets: new Dictionary<string, SecretChoice> { ["enroll_token"] = token });
        var refused = await RefusedAsync(setup, request, "/api/deploy/preview");
        Assert.Equal(ChangeRefusal.Reasons.Invalid, refused.Reason);
        Assert.Contains("needs the secret ts_key when /inputs/features includes \"tailscale\"", refused.Message);
        Assert.Contains(refused.FieldIssues, i => i.Pointer == "/secrets/ts_key");
        Assert.Equal(ChangeRefusal.Reasons.Invalid, (await RefusedAsync(setup, request)).Reason);

        var supplied = await PreviewAsync(setup, request with
        {
            Secrets = new Dictionary<string, SecretChoice> { ["enroll_token"] = token, ["ts_key"] = new(Store: "shared/tailscale-key") },
        });
        Assert.Contains("+  ts_key: { store: shared/tailscale-key }", supplied.Diff);
        await PreviewAsync(setup, request with { Inputs = new JsonObject { ["features"] = new JsonArray("console") } });
    }

    [Theory]
    [InlineData("""{ "pointer": "/inputs/features", "value": "tailscale" }""", """{ "inputs": { "features": ["tailscale"] } }""", true)]
    [InlineData("""{ "pointer": "/inputs/features", "value": "tailscale" }""", """{ "inputs": { "features": ["console"] } }""", false)]
    [InlineData("""{ "pointer": "/inputs/mode", "value": "join" }""", """{ "inputs": { "mode": "join" } }""", true)]
    [InlineData("""{ "pointer": "/inputs/mode", "value": "join" }""", """{ "inputs": {} }""", false)]
    [InlineData("""{ "pointer": "/inputs/a~1b", "value": 2 }""", """{ "inputs": { "a/b": 2 } }""", true)]
    [InlineData("""{ "value": 2 }""", """{ "inputs": {} }""", false)]
    public void RequiredWhen_reads_a_pointer_and_a_value(string hint, string instance, bool required) =>
        Assert.Equal(required, ChangeService.RequiredWhen(JsonNode.Parse(hint), JsonNode.Parse(instance)!.AsObject()) is not null);

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
